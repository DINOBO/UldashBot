using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using UldashBot.Services;

namespace RideShareBot
{
    class Program
    {
        // Вставьте ваш токен ниже
        static string token = "8481717669:AAGZV3qnhxDIjdC0vxqR3FvDQMv7HGWwE6g";

        static async Task Main(string[] args)
        {
            // Инициализируем storage и загружаем существующие данные
            var storage = new Storage("data.json");
            storage.Load();

            // Создаём сервис бота
            var botService = new BotService(token, storage);

            // Запускаем бот
            using var cts = new CancellationTokenSource();
            await botService.StartAsync(cts.Token);

            Console.WriteLine("Нажмите Ctrl+C для остановки...");
            // Блокируем главный поток, пока не придёт отмена
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (TaskCanceledException) { }
        }
    }
}
