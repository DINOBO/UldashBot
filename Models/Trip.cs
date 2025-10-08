using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UldashBot.Models
{
    /// <summary>
    /// Описание рейса
    /// </summary>
    public class Trip
    {
        public int Id { get; set; }
        public long DriverId { get; set; }
        public string DriverName { get; set; } = "";
        public string Car { get; set; } = "-";
        public string Date { get; set; } = ""; // dd.MM
        public string Time { get; set; } = ""; // HH:mm
        public string Departure { get; set; } = "";
        public string Arrival { get; set; } = "";
        public string Price { get; set; }
        public int Seats { get; set; } = 0;
      
        public List<long> Passengers { get; set; } = new List<long>();

        // Для упрощённого сравнения дат/времён — хранится как ISO
        public string GetIsoDateTimeString()
        {
            // Попробуем спарсить Date (dd.MM) и Time (HH:mm), и составить DateTime в текущем/следующем году.
            if (DateTime.TryParseExact($"{Date}.{DateTime.Now.Year} {Time}",
                new[] { "dd.MM.yyyy HH:mm", "d.M.yyyy HH:mm" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out DateTime dt))
            {
                // Если получившаяся дата уже в прошлом, возможно пользователь выбрал дату в начале следующего года — учтём это.
                if (dt < DateTime.Now.AddHours(-1))
                {
                    try
                    {
                        dt = new DateTime(dt.Year + 1, dt.Month, dt.Day, dt.Hour, dt.Minute, 0);
                    }
                    catch { /*если не удалось — оставим как есть*/ }
                }
                return dt.ToString("o"); // ISO 8601
            }
            return DateTime.MinValue.ToString("o");
        }

        public DateTime GetDateTimeOrMin()
        {
            var iso = GetIsoDateTimeString();
            if (DateTime.TryParse(iso, out var dt)) return dt;
            return DateTime.MinValue;
        }
    }
}
