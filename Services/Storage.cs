using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using UldashBot.Models;

namespace UldashBot.Services
{
    /// <summary>
    /// Класс, отвечающий за загрузку/сохранение данных в JSON файл.
    /// Сохраняет сразу после изменения — чтобы минимизировать потерю данных при падении.
    /// </summary>
    public class Storage
    {
        private readonly string _filePath;
        private readonly object _fileLock = new object();

        public StorageModel Model { get; private set; } = new StorageModel();

        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };

        public Storage(string filePath = "data.json")
        {
            _filePath = filePath;
        }

        /// <summary>
        /// Загружает данные из файла, если файл существует.
        /// </summary>
        public void Load()
        {
            lock (_fileLock)
            {
                if (!File.Exists(_filePath))
                {
                    Model = new StorageModel();
                    return;
                }

                try
                {
                    var json = File.ReadAllText(_filePath);
                    Model = JsonSerializer.Deserialize<StorageModel>(json, _jsonOptions) ?? new StorageModel();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Storage] Ошибка при загрузке: {ex.Message}");
                    Model = new StorageModel();
                }
            }
        }

        /// <summary>
        /// Сохраняет модель в файл.
        /// </summary>
        public void Save()
        {
            lock (_fileLock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(Model, _jsonOptions);
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Storage] Ошибка при сохранении: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Вызывается когда что-то поменялось — сохраняем.
        /// </summary>
        public void MarkDirtyAndSave()
        {
            Save();
        }
    }
}
