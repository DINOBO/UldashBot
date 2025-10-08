using RideShareBot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UldashBot.Models
{
    /// <summary>
    /// Модель, сериализуемая в JSON — хранит все необходимые для восстановления данных структуры.
    /// </summary>
    public class StorageModel
    {
        public Dictionary<long, UserProfile> Users { get; set; } = new();
        public Dictionary<int, Trip> Trips { get; set; } = new();
        public Dictionary<int, long> PendingRequests { get; set; } = new();
        public Dictionary<int, int> DriverTripMessageId { get; set; } = new();
        public int TripCounter { get; set; } = 1;
    }
}
