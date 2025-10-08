using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UldashBot.Models
{
    /// <summary>
    /// Пользовательские данные (профиль)
    /// </summary>
    public class UserProfile
    {
        public long ChatId { get; set; }
        public string Name { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Role { get; set; } = ""; // "Водитель" или "Попутчик"
        // Временные поля, используемые в процессе диалога (не критично хранить, но для восстановления может быть полезно)
        public string? Date { get; set; }
        public string? Time { get; set; }
        public string? Car { get; set; }
        public string? Departure { get; set; }
        public string? Arrival { get; set; }
        public string? UserState { get; set; }
    }
}
