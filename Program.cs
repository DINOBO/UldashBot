using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    static string token = "8481717669:AAGZV3qnhxDIjdC0vxqR3FvDQMv7HGWwE6g";
    static TelegramBotClient bot = new TelegramBotClient(token);

    static Dictionary<long, string> userStates = new();
    static Dictionary<long, Dictionary<string, string>> userData = new();
    static Dictionary<int, Dictionary<string, object>> trips = new();
    static Dictionary<int, long> pendingRequests = new();
    static Dictionary<int, int> driverTripMessageId = new();
    static int tripCounter = 1;

    static async Task Main()
    {
        using var cts = new CancellationTokenSource();
        var receiverOptions = new Telegram.Bot.Polling.ReceiverOptions { AllowedUpdates = { } };
        bot.StartReceiving(UpdateHandler, ErrorHandler, receiverOptions, cts.Token);
        Console.WriteLine("Бот запущен...");
        Thread.Sleep(-1);
    }

    static async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        if (update.Type == UpdateType.Message)
        {
            var msg = update.Message;
            var chatId = msg.Chat.Id;

            if (!userStates.ContainsKey(chatId))
            {
                userStates[chatId] = "waiting_name";
                await bot.SendMessage(chatId, "Сәләм! Введите имя:");
                return;
            }

            string state = userStates[chatId];
            string text = msg.Text;

            switch (state)
            {
                case "waiting_name":
                    if (!userData.ContainsKey(chatId)) userData[chatId] = new();
                    userData[chatId]["name"] = text;
                    userStates[chatId] = "waiting_phone";
                    await bot.SendMessage(chatId, "Введите номер телефона:");
                    break;

                case "waiting_phone":
                    userData[chatId]["phone"] = text;
                    userStates[chatId] = "choosing_role";
                    var roleKeyboard = new ReplyKeyboardMarkup(
                        new KeyboardButton[][] { new KeyboardButton[] { "Водитель", "Попутчик" } }
                    )
                    { ResizeKeyboard = true };
                    await bot.SendMessage(chatId, "Выберите роль:", replyMarkup: roleKeyboard);
                    break;

                case "choosing_role":
                    userData[chatId]["role"] = text;
                    await bot.SendMessage(chatId, $"Вы выбрали роль: {text}", replyMarkup: MainMenu(chatId));
                    userStates[chatId] = "main_menu";
                    break;

                case "main_menu":
                    if (text == "Создать рейс" && userData[chatId]["role"] == "Водитель")
                    {
                        userStates[chatId] = "driver_waiting_date";
                        await bot.SendMessage(chatId, "Выберите дату:", replyMarkup: DateKeyboard());
                    }
                    else if (text == "Поиск рейса" && userData[chatId]["role"] == "Попутчик")
                    {
                        userStates[chatId] = "passenger_departure";
                        await bot.SendMessage(chatId, "Введите город отправления:\n");
                    }
                    else if (text == "Выбрать роль")
                    {
                        var roleKeyboard2 = new ReplyKeyboardMarkup(
                            new KeyboardButton[][] { new KeyboardButton[] { "Водитель", "Попутчик" } }
                        )
                        { ResizeKeyboard = true };
                        userStates[chatId] = "choosing_role";
                        await bot.SendMessage(chatId, "Выберите роль:", replyMarkup: roleKeyboard2);
                    }
                    else if (text == "Редактировать данные")
                    {
                        userStates[chatId] = "waiting_name";
                        await bot.SendMessage(chatId, "Введите новое имя:");
                    }
                    else if (text == "Управление рейсами" && userData[chatId]["role"] == "Водитель")
                    {
                        await ShowDriverTrips(chatId);
                    }
                    break;

                case "driver_waiting_time":
                    userData[chatId]["time"] = text;
                    userStates[chatId] = "driver_waiting_car";
                    await bot.SendMessage(chatId, "Введите марку автомобиля:");
                    break;

                case "driver_waiting_car":
                    userData[chatId]["car"] = text;
                    userStates[chatId] = "driver_waiting_departure";
                    await bot.SendMessage(chatId, "Введите город отправления:");
                    break;

                case "driver_waiting_departure":
                    userData[chatId]["departure"] = text;
                    userStates[chatId] = "driver_waiting_arrival";
                    await bot.SendMessage(chatId, "Введите город назначения:");
                    break;

                case "driver_waiting_arrival":
                    userData[chatId]["arrival"] = text;
                    userStates[chatId] = "driver_waiting_seats";
                    await bot.SendMessage(chatId, "Введите количество мест:");
                    break;

                case "driver_waiting_seats":
                    int seats = int.TryParse(text, out int s) ? s : 0;
                    var trip = new Dictionary<string, object>
                    {
                        {"driver_id", chatId},
                        {"driver_name", userData[chatId]["name"]},
                        {"car", userData[chatId].ContainsKey("car") ? userData[chatId]["car"] : "-"},
                        {"date", userData[chatId]["date"]},
                        {"time", userData[chatId]["time"]},
                        {"departure", userData[chatId]["departure"]},
                        {"arrival", userData[chatId]["arrival"]},
                        {"seats", seats},
                        {"passengers", new List<long>()}
                    };
                    trips[tripCounter] = trip;

                    await bot.SendMessage(chatId,
                        $"🚗 *Ваш рейс создан!* 🚗\n\n" +
                        $"*Маршрут:* {userData[chatId]["departure"]} → {userData[chatId]["arrival"]}\n" +
                        $"*Дата и время:* {userData[chatId]["date"]} {userData[chatId]["time"]}\n" +
                        $"*Авто:* {userData[chatId]["car"]}\n" +
                        $"*Мест:* {seats}\n\n" +
                        $"Вы можете управлять рейсом через 'Управление рейсами'.",
                        parseMode: ParseMode.Markdown,
                        replyMarkup: MainMenu(chatId));

                    tripCounter++;
                    userStates[chatId] = "main_menu";
                    break;

                case "passenger_departure":
                    userData[chatId]["departure"] = text;
                    userStates[chatId] = "passenger_arrival";
                    await bot.SendMessage(chatId, "Введите город назначения:\n");
                    break;

                case "passenger_arrival":
                    userData[chatId]["arrival"] = text;
                    userStates[chatId] = "main_menu";
                    await ShowMatchingTrips(chatId);
                    break;

                case "driver_editing_route":
                    int editTripId = int.Parse(userData[chatId]["editing_trip"]);
                    trips[editTripId]["departure"] = text.Split('→')[0].Trim();
                    trips[editTripId]["arrival"] = text.Split('→')[1].Trim();

                    await UpdateDriverTripMessage(editTripId, true);
                    userStates[chatId] = "main_menu";
                    break;
            }
        }

        if (update.Type == UpdateType.CallbackQuery)
        {
            var callback = update.CallbackQuery;
            var chatId = callback.From.Id;
            var data = callback.Data;

            if (userStates[chatId] == "driver_waiting_date" && data.StartsWith("date_"))
            {
                string date = data.Split('_')[1];
                userData[chatId]["date"] = date;
                userStates[chatId] = "driver_waiting_time";
                await bot.SendMessage(chatId, "Введите время отправления в формате ЧЧ:ММ (например 14:30):");
            }
            else if (data.StartsWith("join_"))
            {
                int tripId = int.Parse(data.Split('_')[1]);
                pendingRequests[tripId] = chatId;
                var trip = trips[tripId];
                long driverId = (long)trip["driver_id"];
                await bot.SendMessage(driverId, $"Пассажир {userData[chatId]["name"]} (@{callback.From.Username ?? ""}, {userData[chatId]["phone"]}) хочет присоединиться к рейсу.");
                var kb = new InlineKeyboardMarkup(new[]
                {
                    new []{ InlineKeyboardButton.WithCallbackData("Подтвердить", "confirm_"+tripId), InlineKeyboardButton.WithCallbackData("Отклонить", "reject_"+tripId) }
                });
                await bot.SendMessage(driverId, "Подтвердите запрос:", replyMarkup: kb);
            }
            else if (data.StartsWith("confirm_"))
            {
                int tripId = int.Parse(data.Split('_')[1]);
                if (!pendingRequests.ContainsKey(tripId)) return;
                long passengerId = pendingRequests[tripId];
                var trip = trips[tripId];
                var passengerList = (List<long>)trip["passengers"];
                if ((int)trip["seats"] > 0)
                {
                    passengerList.Add(passengerId);
                    trip["seats"] = (int)trip["seats"] - 1;

                    await bot.SendMessage(passengerId,
                        $"✅ *Ваше место подтверждено!* \n" +
                        $"Маршрут: {trip["departure"]} → {trip["arrival"]}\n" +
                        $"Дата и время: {trip["date"]} {trip["time"]}\n" +
                        $"Водитель: {trip["driver_name"]} | @{(bot.GetChat((long)trip["driver_id"]).Result.Username ?? "")}\n" +
                        $"Телефон водителя: {userData[(long)trip["driver_id"]]["phone"]}",
                        parseMode: ParseMode.Markdown);

                    await UpdateDriverTripMessage(tripId);
                }
                pendingRequests.Remove(tripId);
            }
            else if (data.StartsWith("reject_"))
            {
                int tripId = int.Parse(data.Split('_')[1]);
                if (!pendingRequests.ContainsKey(tripId)) return;
                long passengerId = pendingRequests[tripId];
                await bot.SendMessage(passengerId, $"❌ Ваш запрос отклонен.");
                pendingRequests.Remove(tripId);
            }
            else if (data.StartsWith("delete_"))
            {
                int tripId = int.Parse(data.Split('_')[1]);
                if (trips.ContainsKey(tripId) && (long)trips[tripId]["driver_id"] == chatId)
                {
                    var passengersList = (List<long>)trips[tripId]["passengers"];
                    foreach (var pid in passengersList)
                    {
                        await bot.SendMessage(pid,
                            $"❌ *Рейс отменён!* ❌\n" +
                            $"Водитель {userData[chatId]["name"]} отменил рейс.\n" +
                            $"Маршрут: {trips[tripId]["departure"]} → {trips[tripId]["arrival"]}\n" +
                            $"Дата и время: {trips[tripId]["date"]} {trips[tripId]["time"]}",
                            parseMode: ParseMode.Markdown
                        );
                    }
                    trips.Remove(tripId);
                    driverTripMessageId.Remove(tripId);
                    await bot.SendMessage(chatId, $"🗑️ Рейс удалён.");
                }
            }
            else if (data.StartsWith("edit_"))
            {
                int tripId = int.Parse(data.Split('_')[1]);
                if (trips.ContainsKey(tripId) && (long)trips[tripId]["driver_id"] == chatId)
                {
                    userStates[chatId] = "driver_editing_route";
                    userData[chatId]["editing_trip"] = tripId.ToString();
                    await bot.SendMessage(chatId, "Введите новый маршрут в формате: Откуда → Куда");
                }
            }
            else if (data.StartsWith("cancel_"))
            {
                int tripId = int.Parse(data.Split('_')[1]);
                if (!trips.ContainsKey(tripId)) return;
                long passengerId = callback.From.Id;
                var trip = trips[tripId];
                var passengerList = (List<long>)trip["passengers"];
                if (passengerList.Contains(passengerId))
                {
                    passengerList.Remove(passengerId);
                    trip["seats"] = (int)trip["seats"] + 1;
                    await bot.SendMessage(passengerId, "❌ Вы отменили своё место в рейсе.");
                    await UpdateDriverTripMessage(tripId);
                }
            }
        }
    }

    static async Task ShowMatchingTrips(long chatId)
    {
        string dep = userData[chatId]["departure"];
        string arr = userData[chatId]["arrival"];
        bool found = false;

        foreach (var kv in trips)
        {
            var t = kv.Value;
            string route = $"{t["departure"]} → {t["arrival"]}";
            int seats = (int)t["seats"];
            if (route.Contains(dep) && route.Contains(arr) && seats > 0)
            {
                if (userData[chatId].ContainsKey("date") && t["date"].ToString() != userData[chatId]["date"]) continue;

                found = true;
                var kb = new InlineKeyboardMarkup(new[]
                {
                    new []{ InlineKeyboardButton.WithCallbackData("Выбрать рейс", "join_"+kv.Key) },
                    new []{ InlineKeyboardButton.WithCallbackData("Отменить", "cancel_"+kv.Key) }
                });

                var driverId = (long)t["driver_id"];
                await bot.SendMessage(chatId,
                    $"🚗 *Рейс найден:*\n" +
                    $"Маршрут: {route}\n" +
                    $"Дата и время: {t["date"]} {t["time"]}\n" +
                    $"Водитель: {t["driver_name"]} | @{(bot.GetChat(driverId).Result.Username ?? "")}\n" +
                    $"Телефон водителя: {userData[driverId]["phone"]}\n" +
                    $"Авто: {t["car"]}\n" +
                    $"Осталось мест: {seats}",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: kb);
            }
        }

        if (!found)
            await bot.SendMessage(chatId, "❌ Нет подходящих рейсов.");
    }

    static async Task ShowDriverTrips(long chatId)
    {
        bool found = false;
        foreach (var kv in trips)
        {
            var trip = kv.Value;
            if ((long)trip["driver_id"] == chatId)
            {
                found = true;
                var passengersList = (List<long>)trip["passengers"];
                string passengerInfo = passengersList.Count == 0 ? "Нет пассажиров" : "";
                foreach (var pid in passengersList)
                    passengerInfo += $"\n- {userData[pid]["name"]} | @{(bot.GetChat(pid).Result.Username ?? "")} | {userData[pid]["phone"]}";

                var kb = new InlineKeyboardMarkup(new[]
                {
                    new []{ InlineKeyboardButton.WithCallbackData("Удалить", "delete_"+kv.Key), InlineKeyboardButton.WithCallbackData("Редактировать", "edit_"+kv.Key) }
                });

                string route = $"{trip["departure"]} → {trip["arrival"]}";
                var sentMessage = await bot.SendMessage(chatId,
                    $"🚗 *Ваш рейс:*\n" +
                    $"Маршрут: {route}\n" +
                    $"Дата и время: {trip["date"]} {trip["time"]}\n" +
                    $"Авто: {trip["car"]}\n" +
                    $"Осталось мест: {trip["seats"]}\n\n" +
                    $"*Пассажиры:*{passengerInfo}",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: kb);

                driverTripMessageId[kv.Key] = sentMessage.MessageId;
            }
        }
        if (!found)
            await bot.SendMessage(chatId, "❌ У вас пока нет созданных рейсов.");
    }

    static async Task UpdateDriverTripMessage(int tripId, bool notifyPassengers = false)
    {
        if (!trips.ContainsKey(tripId) || !driverTripMessageId.ContainsKey(tripId)) return;

        var trip = trips[tripId];
        var passengersList = (List<long>)trip["passengers"];
        string passengerInfo = passengersList.Count == 0 ? "Нет пассажиров" : "";
        foreach (var pid in passengersList)
            passengerInfo += $"\n- {userData[pid]["name"]} | @{(bot.GetChat(pid).Result.Username ?? "")} | {userData[pid]["phone"]}";

        var kb = new InlineKeyboardMarkup(new[]
        {
            new []{ InlineKeyboardButton.WithCallbackData("Удалить", "delete_"+tripId), InlineKeyboardButton.WithCallbackData("Редактировать", "edit_"+tripId) }
        });

        string route = $"{trip["departure"]} → {trip["arrival"]}";
        await bot.EditMessageText(
            chatId: (long)trip["driver_id"],
            messageId: driverTripMessageId[tripId],
            text: $"🚗 *Ваш рейс:*\n" +
                  $"Маршрут: {route}\n" +
                  $"Дата и время: {trip["date"]} {trip["time"]}\n" +
                  $"Авто: {trip["car"]}\n" +
                  $"Осталось мест: {trip["seats"]}\n\n" +
                  $"*Пассажиры:*{passengerInfo}",
            parseMode: ParseMode.Markdown,
            replyMarkup: kb);
    }

    static ReplyKeyboardMarkup MainMenu(long chatId)
    {
        if (!userData.ContainsKey(chatId)) return null;

        string role = userData[chatId].ContainsKey("role") ? userData[chatId]["role"] : "";
        if (role == "Водитель")
        {
            return new ReplyKeyboardMarkup(
                new KeyboardButton[][]
                {
                    new KeyboardButton[]{ new KeyboardButton("Создать рейс") },
                    new KeyboardButton[]{ new KeyboardButton("Управление рейсами") },
                    new KeyboardButton[]{ new KeyboardButton("Выбрать роль") },
                    new KeyboardButton[]{ new KeyboardButton("Редактировать данные") }
                })
            { ResizeKeyboard = true };
        }
        else
        {
            return new ReplyKeyboardMarkup(
                new KeyboardButton[][]
                {
                    new KeyboardButton[]{ new KeyboardButton("Поиск рейса") },
                    new KeyboardButton[]{ new KeyboardButton("Выбрать роль") },
                    new KeyboardButton[]{ new KeyboardButton("Редактировать данные") }
                })
            { ResizeKeyboard = true };
        }
    }

    static InlineKeyboardMarkup DateKeyboard()
    {
        var buttons = new List<InlineKeyboardButton[]>();
        DateTime today = DateTime.Today;
        var row = new List<InlineKeyboardButton>();
        for (int i = 0; i < 14; i++)
        {
            DateTime d = today.AddDays(i);
            string text = d.ToString("dd.MM");
            row.Add(InlineKeyboardButton.WithCallbackData(text, "date_" + text));
            if (row.Count == 5)
            {
                buttons.Add(row.ToArray());
                row = new List<InlineKeyboardButton>();
            }
        }
        if (row.Count > 0) buttons.Add(row.ToArray());
        return new InlineKeyboardMarkup(buttons);
    }

    static Task ErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
    {
        Console.WriteLine(exception.Message);
        return Task.CompletedTask;
    }
}
