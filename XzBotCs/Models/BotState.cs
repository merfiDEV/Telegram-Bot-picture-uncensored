using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace XzBotCs.Models
{
    public class BotState
    {
        public int UsageCount { get; set; }
        public int ErrorCount { get; set; }
        public bool IsWatermarkEnabled { get; set; } = false;
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public List<double> ResponseTimesMs { get; set; } = new List<double>();
        public Dictionary<string, int> ErrorDetails { get; set; } = new Dictionary<string, int>();
        public List<RequestRecord> RecentRequests { get; set; } = new List<RequestRecord>();
        public Dictionary<string, string> WatermarkFileIds { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> DailyUsage { get; set; } = new Dictionary<string, int>();

        private static string FilePath = "bot_state.json";

        public static BotState Load()
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    string json = File.ReadAllText(FilePath);
                    var state = JsonConvert.DeserializeObject<BotState>(json);
                    if (state != null)
                    {
                        state.StartedAt = DateTime.Now; // Reset uptime on restart
                        return state;
                    }
                }
                catch { }
            }
            return new BotState();
        }

        public void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }

    public class RequestRecord
    {
        public DateTime Time { get; set; }
        public long UserId { get; set; }
        public string Username { get; set; } = "Unknown";
        public string Query { get; set; } = string.Empty;
        public bool Success { get; set; }
    }
}
