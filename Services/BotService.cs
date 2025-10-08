using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using UldashBot.Models;

namespace UldashBot.Services
{
    public class BotService
    {
        private readonly TelegramBotClient _bot;
        private readonly Storage _storage;
        private readonly object _stateLock = new(); // защита для пользовательских состояний и модификаций
        private readonly Dictionary<long, string> _userStates = new(); // runtime состояния (не все сохраняем)
        private System.Threading.Timer? _cleanupTimer;

        // Ограничение: максимум активных рейсов у водителя
        private const int MaxTripsPerDriver = 2;

        public BotService(string token, Storage storage)
        {
            _bot = new TelegramBotClient(token);
            _storage = storage;
        }

        /// <summary>
        /// Запуск: загрузка данных, настройка таймера очистки, запуск получения обновлений.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine($"Бот запускается.");

            // Удаляем давно просроченные рейсы при старте
            RemoveExpiredTrips();

            // Таймер регулярно проверяет и удаляет просроченные рейсы (каждую минуту)
            _cleanupTimer = new System.Threading.Timer(_ => {
                try
                {
                    RemoveExpiredTrips();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Timer] Ошибка очистки: {ex.Message}");
                }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            // Подписываемся на события ProcessExit/CancelKeyPress чтобы сохранить состояние при выходе
            AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                Console.WriteLine("[Shutdown] Process exit — сохраняем данные...");
                _storage.MarkDirtyAndSave();
            };
            Console.CancelKeyPress += (s, e) => {
                Console.WriteLine("[Shutdown] CancelKeyPress — сохраняем данные...");
                _storage.MarkDirtyAndSave();
            };

            // StartReceiving
            var receiverOptions = new Telegram.Bot.Polling.ReceiverOptions
            {
                AllowedUpdates = { } // receive all
            };

            _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, cancellationToken);
            Console.WriteLine("Бот запущен и слушает обновления...");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Обработчик ошибок получения апдейтов.
        /// </summary>
        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
        {
            var err = exception switch
            {
                ApiRequestException apiEx => $"Telegram API Error:\n[{apiEx.ErrorCode}] {apiEx.Message}",
                _ => exception.ToString()
            };
            Console.WriteLine(err);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Основной обработчик апдейтов (сообщения + callback queries).
        /// </summary>
        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
        {
            try
            {
                if (update.Type == UpdateType.Message && update.Message is { } msg)
                {
                    await HandleMessage(msg);
                }
                else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } cb)
                {
                    await HandleCallback(cb);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HandleUpdate] Ошибка: {ex.Message}");
                // При ошибке — сохраняем состояние
                _storage.MarkDirtyAndSave();
            }
        }

        // ----------------- ОБРАБОТКА СООБЩЕНИЙ -----------------

        private async Task HandleMessage(Message msg)
        {
            var chatId = msg.Chat.Id;
            var text = msg.Text ?? "";
            bool newPorfile = false;

            lock (_stateLock)
            {
                if (!_storage.Model.Users.ContainsKey(chatId))
                {
                    // Инициализируем профиль и стартовое состояние
                    _storage.Model.Users[chatId] = new UserProfile { ChatId = chatId };
                    newPorfile = true;
                }

                if (!_userStates.ContainsKey(chatId))
                {
                    if (newPorfile)
                        _userStates[chatId] = "waiting_name";
                    else
                        _userStates[chatId] = "main_menu";
                }
            }
            if (newPorfile)
            {
                await _bot.SendMessage(chatId, "Сәләм! Введите имя:");
                return;
            }


            var state = GetUserState(chatId);

            switch (state)
            {
                case "waiting_name":
                    {
                        _storage.Model.Users[chatId].Name = text;
                        SetUserState(chatId, "waiting_phone");
                        await _bot.SendMessage(chatId, "Введите номер телефона:");
                        _storage.MarkDirtyAndSave();
                        break;
                    }
                case "waiting_phone":
                    {
                        _storage.Model.Users[chatId].Phone = text;
                        SetUserState(chatId, "choosing_role");
                        var roleKeyboard = new ReplyKeyboardMarkup(new[]
                        {
                            new KeyboardButton[] { "Водитель", "Попутчик" }
                        })
                        { ResizeKeyboard = true };
                        await _bot.SendMessage(chatId, "Выберите роль:", replyMarkup: roleKeyboard);
                        _storage.MarkDirtyAndSave();
                        break;
                    }
                case "choosing_role":
                    {
                        _storage.Model.Users[chatId].Role = text;
                        SetUserState(chatId, "main_menu");
                        await _bot.SendMessage(chatId, $"Вы выбрали роль: {text}", replyMarkup: MainMenu(chatId));
                        _storage.MarkDirtyAndSave();
                        break;
                    }
                case "main_menu":
                    {
                        if (text == "Создать рейс" && _storage.Model.Users[chatId].Role == "Водитель")
                        {
                            // Проверить лимит водителя (только будущие рейсы)
                            int active = CountActiveTripsForDriver(chatId);
                            if (active >= MaxTripsPerDriver)
                            {
                                await _bot.SendMessage(chatId, $"❌ Нельзя создавать больше {MaxTripsPerDriver} активных рейсов. Удалите или дождитесь окончания одного из текущих рейсов.", replyMarkup: MainMenu(chatId));
                                return;
                            }

                            SetUserState(chatId, "driver_waiting_date");
                            await _bot.SendMessage(chatId, "Выберите дату:", replyMarkup: DateKeyboard());
                        }
                        else if (text == "Поиск рейсов" && _storage.Model.Users[chatId].Role == "Попутчик")
                        {
                            SetUserState(chatId, "passenger_departure");
                            // Показываем кнопки с городами
                            var keyboard = new InlineKeyboardMarkup(new[]
                            {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Уфа", "Уфа"),
            InlineKeyboardButton.WithCallbackData("Инзер", "Инзер")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Белорецк", "Белорецк"),
            InlineKeyboardButton.WithCallbackData("Аскарово", "Аскарово")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Магнитогорск", "Магнитогорск")
        }
    });

                            await _bot.SendMessage(chatId, "Откуда:", replyMarkup: keyboard);
                        }
                        else if (text == "Выбрать роль")
                        {
                            SetUserState(chatId, "choosing_role");
                            var roleKeyboard2 = new ReplyKeyboardMarkup(new[]
                            {
                                new KeyboardButton[] { "Водитель", "Попутчик" }
                            })
                            { ResizeKeyboard = true };
                            await _bot.SendMessage(chatId, "Выберите роль:", replyMarkup: roleKeyboard2);
                        }
                        else if (text == "Мои данные")
                        {
                            var roleKeyboard2 = new ReplyKeyboardMarkup(new[]
                            {
                                new KeyboardButton[] { "Изменить", "Назад в меню" }
                            })
                            { ResizeKeyboard = true };
                            await _bot.SendMessage(chatId, $"Ваше имя: {_storage.Model.Users[chatId].Name}, ваш номер: {_storage.Model.Users[chatId].Phone}", replyMarkup: roleKeyboard2);
                        }
                        else if (text == "Изменить")
                        {
                            SetUserState(chatId, "waiting_name");
                            await _bot.SendMessage(chatId, "Введите имя:");
                            _storage.MarkDirtyAndSave();
                            break;
                        }
                        else if (text == "Назад в меню")
                        {
                            SetUserState(chatId, "main_menu");
                            await _bot.SendMessage(chatId, "Выберите функцию:", replyMarkup: MainMenu(chatId));
                            _storage.MarkDirtyAndSave();
                            break;
                        }
                        else if (text == "Управление рейсами" && _storage.Model.Users[chatId].Role == "Водитель")
                        {
                            await ShowDriverTrips(chatId);
                        }
                        else
                        {
                            await _bot.SendMessage(chatId, "Пожалуйста, пользуйтесь меню", replyMarkup: MainMenu(chatId));
                        }
                        break;
                    }
                case "driver_waiting_departure":
                    {
                        _storage.Model.Users[chatId].Departure = text; 
                        SetUserState(chatId, "driver_waiting_arrival");
                        // Показываем кнопки с городами
                        var keyboard = new InlineKeyboardMarkup(new[]
                        {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Уфа", "Уфа"),
            InlineKeyboardButton.WithCallbackData("Инзер", "Инзер")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Белорецк", "Белорецк"),
            InlineKeyboardButton.WithCallbackData("Аскарово", "Аскарово")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Магнитогорск", "Магнитогорск")
        }
    });

                        await _bot.SendMessage(chatId, "Куда:", replyMarkup: keyboard);
                        break;
                    }
                case "driver_waiting_arrival":
                    {
                        _storage.Model.Users[chatId].Arrival = text;
                        SetUserState(chatId, "driver_waiting_time");
                        await _bot.SendMessage(chatId, "Введите время и место отправления (например, 15:00 Центральный Автовокзал):");
                        _storage.MarkDirtyAndSave();
                        break;
                    }
                case "driver_waiting_time":
                    {
                        _storage.Model.Users[chatId].Time = text;
                        SetUserState(chatId, "driver_waiting_car");
                        await _bot.SendMessage(chatId, "Введите марку автомобиля:");
                        _storage.MarkDirtyAndSave();
                        break;
                    }
                case "driver_waiting_car":
                    {
                        _storage.Model.Users[chatId].Car = text;
                        SetUserState(chatId, "driver_waiting_price");
                        await _bot.SendMessage(chatId, "Цена проезда:");
                        _storage.MarkDirtyAndSave();
                        break;
                    }
                case "driver_waiting_price":
                    {
                        _storage.Model.Users[chatId].Price = text;
                        SetUserState(chatId, "driver_waiting_seats");
                        await _bot.SendMessage(chatId, "Количество мест:");
                        _storage.MarkDirtyAndSave();
                        break;
                    }
                case "driver_waiting_seats":
                    {
                        if (!int.TryParse(text, out int seats)) seats = 0;

                        var profile = _storage.Model.Users[chatId];

                        // Последняя проверка лимита активных рейсов (на всякий случай)
                        int active = CountActiveTripsForDriver(chatId);
                        if (active >= MaxTripsPerDriver)
                        {
                            await _bot.SendMessage(chatId, $"❌ Нельзя создавать больше {MaxTripsPerDriver} активных рейсов. Удалите или дождитесь окончания одного из текущих рейсов.", replyMarkup: MainMenu(chatId));
                            SetUserState(chatId, "main_menu");
                            return;
                        }

                        var trip = new Trip
                        {
                            Id = _storage.Model.TripCounter,
                            DriverId = chatId,
                            DriverName = profile.Name,
                            Car = profile.Car ?? "-",
                            Date = profile.Date ?? DateTime.Today.ToString("dd.MM"),
                            Time = profile.Time ?? DateTime.Now.ToString("HH:mm"),
                            Departure = profile.Departure ?? "-",
                            Arrival = profile.Arrival ?? "-",
                            Price = profile.Price,
                            Seats = seats,
                            Passengers = new List<long>()
                        };

                        // Добавляем и инкрементируем счётчик
                        _storage.Model.Trips[trip.Id] = trip;
                        _storage.Model.TripCounter++;
                        _storage.MarkDirtyAndSave();

                        string message =
                            $"🚗 *Ваш рейс создан!* 🚗\n\n" +
                            $"*Маршрут:* {trip.Departure} → {trip.Arrival}\n" +
                            $"*Дата и время:* {trip.Date} {trip.Time}\n" +
                            $"*Авто:* {trip.Car}\n" +
                            $"*Цена:* {trip.Price}\n" +
                            $"*Мест:* {trip.Seats}\n\n" +
                            $"Вы можете управлять рейсом через 'Управление рейсами'.";

                        await _bot.SendMessage(chatId, message, ParseMode.Markdown, replyMarkup: MainMenu(chatId));

                        SetUserState(chatId, "main_menu");
                        break;
                    }
                case "passenger_departure":
                    {
                        _storage.Model.Users[chatId].Departure = text;
                        SetUserState(chatId, "passenger_arrival");
                        // Показываем кнопки с городами
                        var keyboard = new InlineKeyboardMarkup(new[]
                        {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Уфа", "Уфа"),
            InlineKeyboardButton.WithCallbackData("Инзер", "Инзер")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Белорецк", "Белорецк"),
            InlineKeyboardButton.WithCallbackData("Аскарово", "Аскарово")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Магнитогорск", "Магнитогорск")
        }
    });

                        await _bot.SendMessage(chatId, "Куда:", replyMarkup: keyboard);
                        _storage.MarkDirtyAndSave();
                        break;
                    }
                case "passenger_arrival":
                    {
                        _storage.Model.Users[chatId].Arrival = text;
                        SetUserState(chatId, "main_menu");
                        _storage.MarkDirtyAndSave();
                        await ShowMatchingTrips(chatId);
                        break;
                    }
                case "driver_editing_route":
                    {
                        // Ожидается, что editing_trip хранится во временных полях — используем поле Car как временное хранилище editing_trip id (можно изменить)
                        // Чтобы не менять UserProfile, используем user state temp storage: store "editing_trip_{tripId}" в state
                        state = GetUserState(chatId);
                        if (state.StartsWith("driver_editing_route_"))
                        {
                            if (int.TryParse(state.Split('_').Last(), out int editTripId))
                            {
                                var parts = text.Split('→');
                                if (parts.Length >= 2)
                                {
                                    lock (_stateLock)
                                    {
                                        if (_storage.Model.Trips.ContainsKey(editTripId))
                                        {
                                            _storage.Model.Trips[editTripId].Departure = parts[0].Trim();
                                            _storage.Model.Trips[editTripId].Arrival = parts[1].Trim();
                                            _storage.MarkDirtyAndSave();
                                        }
                                    }
                                    await UpdateDriverTripMessage(editTripId, true);
                                }
                            }
                        }
                        SetUserState(chatId, "main_menu");
                        break;
                    }
                default:
                    {
                        // Неизвестное состояние — сбрасываем в основное меню
                        SetUserState(chatId, "main_menu");
                        await _bot.SendMessage(chatId, "Используйте меню:", replyMarkup: MainMenu(chatId));
                        break;
                    }
            }
        }

        // ----------------- ОБРАБОТКА CALLBACK QUERY -----------------

        private async Task HandleCallback(CallbackQuery cb)
        {
            var chatId = cb.From.Id;
            var data = cb.Data ?? "";

            // Сначала проверим кейс выбора даты (driver_waiting_date)
            if (GetUserState(chatId) == "driver_waiting_date" && data.StartsWith("date_"))
            {
                string date = data.Split('_')[1];
                _storage.Model.Users[chatId].Date = date;
                SetUserState(chatId, "driver_waiting_departure");
                // Показываем кнопки с городами
                var keyboard = new InlineKeyboardMarkup(new[]
                {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Уфа", "Уфа"),
            InlineKeyboardButton.WithCallbackData("Инзер", "Инзер")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Белорецк", "Белорецк"),
            InlineKeyboardButton.WithCallbackData("Аскарово", "Аскарово")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Магнитогорск", "Магнитогорск")
        }
    });

                await _bot.SendMessage(chatId, "Откуда:", replyMarkup: keyboard);
                _storage.MarkDirtyAndSave();
                return;
            }

            if (data.StartsWith("join_"))
            {
                if (!int.TryParse(data.Split('_')[1], out int tripId)) return;
                if (!_storage.Model.Trips.ContainsKey(tripId))
                {
                    await _bot.SendMessage(chatId, "Рейс не найден или уже завершён.");
                    return;
                }

                // Проверяем повторный запрос — pendingRequests хранит последний запрос на рейс
                if (_storage.Model.PendingRequests.ContainsKey(tripId) && _storage.Model.PendingRequests[tripId] == chatId)
                {
                    await _bot.SendMessage(chatId, "Вы уже отправили запрос на этот рейс!");
                    return;
                }

                _storage.Model.PendingRequests[tripId] = chatId;
                _storage.MarkDirtyAndSave();

                var trip = _storage.Model.Trips[tripId];
                long driverId = trip.DriverId;
                var passengerProfile = _storage.Model.Users.ContainsKey(chatId) ? _storage.Model.Users[chatId] : new UserProfile { ChatId = chatId, Name = "Пассажир", Phone = "" };

                await _bot.SendMessage(driverId,
                    $"Пассажир {passengerProfile.Name} (@{cb.From.Username ?? ""}, {passengerProfile.Phone}) хочет присоединиться к рейсу.");

                var kb = new InlineKeyboardMarkup(new[]
                {
                    new []{ InlineKeyboardButton.WithCallbackData("Подтвердить", "confirm_"+tripId), InlineKeyboardButton.WithCallbackData("Отклонить", "reject_"+tripId) }
                });
                await _bot.SendMessage(driverId, "Подтвердите запрос:", replyMarkup: kb);
                await _bot.SendMessage(chatId, "Запрос отправлен, ожидайте подтверждения!");
                return;
            }
            else if (data.StartsWith("confirm_"))
            {
                if (!int.TryParse(data.Split('_')[1], out int tripId)) return;
                if (!_storage.Model.PendingRequests.ContainsKey(tripId)) return;

                var passengerId = _storage.Model.PendingRequests[tripId];
                if (!_storage.Model.Trips.ContainsKey(tripId))
                {
                    await _bot.SendMessage(passengerId, "Рейс не найден или был отменён.");
                    _storage.Model.PendingRequests.Remove(tripId);
                    _storage.MarkDirtyAndSave();
                    return;
                }

                var trip = _storage.Model.Trips[tripId];
                if (trip.Seats > 0)
                {
                    trip.Passengers.Add(passengerId);
                    trip.Seats -= 1;

                    // Отправляем пассажиру подтверждение
                    var driverProfile = _storage.Model.Users.ContainsKey(trip.DriverId) ? _storage.Model.Users[trip.DriverId] : new UserProfile { Name = "Водитель", Phone = "" };
                    string msg =
                        $"✅ *Ваше место подтверждено!* \n" +
                        $"Маршрут: {trip.Departure} → {trip.Arrival}\n" +
                        $"Дата и время: {trip.Date} {trip.Time}\n" +
                        $"Водитель: {trip.DriverName} | @{((_bot.GetChat(trip.DriverId).Result.Username ?? "").Replace("_", "\\_"))}\n" +
                        $"Телефон водителя: {driverProfile.Phone}";
                    await _bot.SendMessage(passengerId, msg, ParseMode.Markdown);

                    _storage.MarkDirtyAndSave();

                    // Обновляем сообщение водителя
                    await UpdateDriverTripMessage(tripId);
                }
                else
                {
                    await _bot.SendMessage(passengerId, "К сожалению, мест нет.");
                }

                _storage.Model.PendingRequests.Remove(tripId);
                _storage.MarkDirtyAndSave();
                return;
            }
            else if (data.StartsWith("reject_"))
            {
                if (!int.TryParse(data.Split('_')[1], out int tripId)) return;
                if (!_storage.Model.PendingRequests.ContainsKey(tripId)) return;

                var passengerId = _storage.Model.PendingRequests[tripId];
                await _bot.SendMessage(passengerId, $"❌ Ваш запрос отклонен.");
                _storage.Model.PendingRequests.Remove(tripId);
                _storage.MarkDirtyAndSave();
                return;
            }
            else if (data.StartsWith("delete_"))
            {
                if (!int.TryParse(data.Split('_')[1], out int tripId)) return;
                if (!_storage.Model.Trips.ContainsKey(tripId)) return;

                var trip = _storage.Model.Trips[tripId];
                if (trip.DriverId != chatId) // проверка прав
                {
                    await _bot.SendMessage(chatId, "Вы не являетесь владельцем этого рейса.");
                    return;
                }

                // Уведомляем пассажиров
                foreach (var pid in trip.Passengers.ToList())
                {
                    if (_storage.Model.Users.ContainsKey(pid))
                    {
                        await _bot.SendMessage(pid,
                            $"❌ *Рейс отменён!* ❌\n" +
                            $"Водитель {trip.DriverName} отменил рейс.\n" +
                            $"Маршрут: {trip.Departure} → {trip.Arrival}\n" +
                            $"Дата и время: {trip.Date} {trip.Time}",
                            ParseMode.Markdown);
                    }
                }

                _storage.Model.Trips.Remove(tripId);
                if (_storage.Model.DriverTripMessageId.ContainsKey(tripId))
                    _storage.Model.DriverTripMessageId.Remove(tripId);

                _storage.MarkDirtyAndSave();
                await _bot.SendMessage(chatId, $"🗑️ Рейс удалён.");
                return;
            }
            else if (data.StartsWith("edit_"))
            {
                if (!int.TryParse(data.Split('_')[1], out int tripId)) return;
                if (!_storage.Model.Trips.ContainsKey(tripId)) return;

                var trip = _storage.Model.Trips[tripId];
                if (trip.DriverId != chatId)
                {
                    await _bot.SendMessage(chatId, "Вы не являетесь владельцем этого рейса.");
                    return;
                }

                // Устанавливаем state "driver_editing_route_{tripId}" и просим ввести "Откуда → Куда"
                SetUserState(chatId, $"driver_editing_route_{tripId}");
                await _bot.SendMessage(chatId, "Введите новый маршрут в формате: Откуда → Куда");
                return;
            }
            else if (data.StartsWith("cancel_"))
            {
                if (!int.TryParse(data.Split('_')[1], out int tripId)) return;
                if (!_storage.Model.Trips.ContainsKey(tripId)) return;

                long passengerId = cb.From.Id;
                var trip = _storage.Model.Trips[tripId];
                var passengerList = trip.Passengers;
                if (passengerList.Contains(passengerId))
                {
                    passengerList.Remove(passengerId);
                    trip.Seats += 1;
                    _storage.MarkDirtyAndSave();
                    await _bot.SendMessage(passengerId, "❌ Вы отменили своё место в рейсе.");
                    await UpdateDriverTripMessage(tripId);
                }
                return;
            }
        }

        // ----------------- УТИЛИТЫ: вывод рейсов и обновления -----------------

        private async Task ShowMatchingTrips(long chatId)
        {
            var user = _storage.Model.Users[chatId];
            string dep = user.Departure ?? "";
            string arr = user.Arrival ?? "";
            bool found = false;

            // Чистим просроченные перед поиском
            RemoveExpiredTrips();

            foreach (var kv in _storage.Model.Trips.ToList())
            {
                var t = kv.Value;
                string route = $"{t.Departure} → {t.Arrival}";
                int seats = t.Seats;
                if (string.IsNullOrEmpty(dep) || string.IsNullOrEmpty(arr)) continue;

                // Условие совпадения: маршрут содержит оба значения (простая логика как в оригинале)
                if (route.Contains(dep, StringComparison.OrdinalIgnoreCase) && route.Contains(arr, StringComparison.OrdinalIgnoreCase) && seats > 0)
                {
                    // Если пользователь указал дату — сопоставим
                    //if (!string.IsNullOrEmpty(user.Date) && t.Date != user.Date) continue;

                    found = true;
                    var kb = new InlineKeyboardMarkup(new[]
                    {
                        new []{ InlineKeyboardButton.WithCallbackData("Выбрать рейс", "join_"+kv.Key) }
                        //new []{ InlineKeyboardButton.WithCallbackData("Отменить", "cancel_"+kv.Key) }
                    });

                    var driverId = t.DriverId;
                    string driverPhone = _storage.Model.Users.ContainsKey(driverId) ? _storage.Model.Users[driverId].Phone : "";

                    string message =
                        $"🚗 *Рейс найден:*\n" +
                        $"Маршрут: {route}\n" +
                        $"Дата и время: {t.Date} {t.Time}\n" +
                        $"Водитель: {t.DriverName} | @{((_bot.GetChat(driverId).Result.Username ?? "").Replace("_", "\\_"))}\n" +
                        $"Телефон водителя: {driverPhone}\n" +
                        $"Авто: {t.Car}\n" +
                        $"Цена: {t.Price}\n" +
                        $"Осталось мест: {seats}";

                    await _bot.SendMessage(chatId, message, ParseMode.Markdown, replyMarkup: kb);
                }
            }

            if (!found)
                await _bot.SendMessage(chatId, "❌ Нет подходящих рейсов.");
        }

        private async Task ShowDriverTrips(long chatId)
        {
            bool found = false;

            // Чистим просроченные перед показом
            RemoveExpiredTrips();

            foreach (var kv in _storage.Model.Trips.ToList())
            {
                var trip = kv.Value;
                if (trip.DriverId == chatId)
                {
                    found = true;
                    var passengersList = trip.Passengers;
                    string passengerInfo = passengersList.Count == 0 ? "Нет пассажиров" : "";
                    foreach (var pid in passengersList)
                    {
                        if (_storage.Model.Users.ContainsKey(pid))
                            passengerInfo += $"\n- {_storage.Model.Users[pid].Name} | @{(_bot.GetChat(pid).Result.Username ?? "")} | {_storage.Model.Users[pid].Phone}";
                    }

                    var kb = new InlineKeyboardMarkup(new[]
                    {
                        new []{ InlineKeyboardButton.WithCallbackData("Удалить", "delete_"+kv.Key), InlineKeyboardButton.WithCallbackData("Редактировать", "edit_"+kv.Key) }
                    });

                    string route = $"{trip.Departure} → {trip.Arrival}";
                    var sentMessage = await _bot.SendMessage(chatId,
                        $"🚗 *Ваш рейс:*\n" +
                        $"Маршрут: {route}\n" +
                        $"Дата и время: {trip.Date} {trip.Time}\n" +
                        $"Авто: {trip.Car}\n" +
                        $"Цена: {trip.Price}\n" +
                        $"Осталось мест: {trip.Seats}\n\n" +
                        $"*Пассажиры:*{passengerInfo}",
                        ParseMode.Markdown,
                        replyMarkup: kb);

                    _storage.Model.DriverTripMessageId[kv.Key] = sentMessage.MessageId;
                    _storage.MarkDirtyAndSave();
                }
            }
            if (!found)
                await _bot.SendMessage(chatId, "❌ У вас пока нет созданных рейсов.");
        }

        private async Task UpdateDriverTripMessage(int tripId, bool notifyPassengers = false)
        {
            if (!_storage.Model.Trips.ContainsKey(tripId)) return;
            if (!_storage.Model.DriverTripMessageId.ContainsKey(tripId)) return;

            var trip = _storage.Model.Trips[tripId];
            var passengersList = trip.Passengers;
            string passengerInfo = passengersList.Count == 0 ? "Нет пассажиров" : "";
            foreach (var pid in passengersList)
            {
                if (_storage.Model.Users.ContainsKey(pid))
                    passengerInfo += $"\n- {_storage.Model.Users[pid].Name} | @{(_bot.GetChat(pid).Result.Username ?? "")} | {_storage.Model.Users[pid].Phone}";
            }

            var kb = new InlineKeyboardMarkup(new[]
            {
                new []{ InlineKeyboardButton.WithCallbackData("Удалить", "delete_"+tripId), InlineKeyboardButton.WithCallbackData("Редактировать", "edit_"+tripId) }
            });

            string route = $"{trip.Departure} → {trip.Arrival}";
            try
            {
                await _bot.EditMessageText(
                    chatId: trip.DriverId,
                    messageId: _storage.Model.DriverTripMessageId[tripId],
                    text: $"🚗 *Ваш рейс:*\n" +
                          $"Маршрут: {route}\n" +
                          $"Дата и время: {trip.Date} {trip.Time}\n" +
                          $"Авто: {trip.Car}\n" +
                          $"Цена: {trip.Price}\n" +
                          $"Осталось мест: {trip.Seats}\n\n" +
                          $"*Пассажиры:*{passengerInfo}",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: kb);
            }
            catch (ApiRequestException)
            {
                // Не обязательно фатальная ошибка — сообщение могло быть удалено вручную.
                Console.WriteLine($"[UpdateDriverTripMessage] Не удалось отредактировать сообщение для рейса {tripId}");
            }
        }

        // ----------------- ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ -----------------

        private ReplyKeyboardMarkup MainMenu(long chatId)
        {
            var role = _storage.Model.Users.ContainsKey(chatId) ? _storage.Model.Users[chatId].Role : "";
            if (role == "Водитель")
            {
                return new ReplyKeyboardMarkup(new[]
                {
                    new KeyboardButton[] { new KeyboardButton("Создать рейс") },
                    new KeyboardButton[] { new KeyboardButton("Управление рейсами") },
                    new KeyboardButton[] { new KeyboardButton("Выбрать роль") },
                    new KeyboardButton[] { new KeyboardButton("Мои данные") }
                })
                { ResizeKeyboard = true };
            }
            else
            {
                return new ReplyKeyboardMarkup(new[]
                {
                    new KeyboardButton[] { new KeyboardButton("Поиск рейсов") },
                    new KeyboardButton[] { new KeyboardButton("Выбрать роль") },
                    new KeyboardButton[] { new KeyboardButton("Мои данные") }
                })
                { ResizeKeyboard = true };
            }
        }

        private InlineKeyboardMarkup DateKeyboard()
        {
            var buttons = new List<InlineKeyboardButton[]>();
            DateTime today = DateTime.Today;
            var row = new List<InlineKeyboardButton>();
            for (int i = 0; i < 7; i++)
            {
                DateTime d = today.AddDays(i);
                string text = d.ToString("dd.MM");
                row.Add(InlineKeyboardButton.WithCallbackData(text, "date_" + text));
                if (row.Count == 4)
                {
                    buttons.Add(row.ToArray());
                    row = new List<InlineKeyboardButton>();
                }
            }
            if (row.Count > 0) buttons.Add(row.ToArray());
            return new InlineKeyboardMarkup(buttons);
        }

        /// <summary>
        /// Удаляет рейсы, если их дата/время уже прошли.
        /// Уведомляет пассажиров при удалении (опционально можно добавлять причину).
        /// </summary>
        private void RemoveExpiredTrips()
        {
            lock (_stateLock)
            {
                var now = DateTime.Now;
                var expired = _storage.Model.Trips.Values.Where(t => t.GetDateTimeOrMin() != DateTime.MinValue && t.GetDateTimeOrMin() < now).Select(t => t.Id).ToList();
                if (expired.Count == 0) return;

                foreach (var id in expired)
                {
                    var trip = _storage.Model.Trips[id];
                    // Уведомляем пассажиров, что рейс удалён из-за прошедшего времени (если нужно)
                    foreach (var pid in trip.Passengers)
                    {
                        try
                        {
                            _bot.SendMessage(pid,
                                $"ℹ️ Рейс {trip.Departure} → {trip.Arrival} от {trip.Date} {trip.Time} завершён и удалён.");
                        }
                        catch { /*ignore*/ }
                    }
                    _storage.Model.Trips.Remove(id);
                    if (_storage.Model.DriverTripMessageId.ContainsKey(id))
                        _storage.Model.DriverTripMessageId.Remove(id);
                }
                _storage.MarkDirtyAndSave();
                Console.WriteLine($"[Cleanup] Удалено {expired.Count} просроченных рейсов.");
            }
        }

        private int CountActiveTripsForDriver(long driverId)
        {
            var now = DateTime.Now;
            return _storage.Model.Trips.Values.Count(t => t.DriverId == driverId && t.GetDateTimeOrMin() != DateTime.MinValue && t.GetDateTimeOrMin() >= now);
        }

        private string GetUserState(long chatId)
        {
            lock (_stateLock)
            {
                if (!_userStates.ContainsKey(chatId)) _userStates[chatId] = "waiting_name";
                return _userStates[chatId];
            }
        }

        private void SetUserState(long chatId, string state)
        {
            lock (_stateLock)
            {
                _userStates[chatId] = state;
            }
        }
    }
}
