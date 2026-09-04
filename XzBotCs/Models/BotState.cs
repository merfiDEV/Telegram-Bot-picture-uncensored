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
        public string WatermarkText { get; set; } = "Грешок by MDEV";
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public List<double> ResponseTimesMs { get; set; } = new List<double>();
        public Dictionary<string, int> ErrorDetails { get; set; } = new Dictionary<string, int>();
        public List<RequestRecord> RecentRequests { get; set; } = new List<RequestRecord>();
        public Dictionary<string, string> WatermarkFileIds { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> DailyUsage { get; set; } = new Dictionary<string, int>();
        public HashSet<long> Subscribers { get; set; } = new HashSet<long>();
        public HashSet<long> ExtraAdmins { get; set; } = new HashSet<long>();
        public bool AllowNsfw { get; set; } = false;
        public Dictionary<long, bool> UserNsfwSettings { get; set; } = new Dictionary<long, bool>();

        // Новые поля для статистики дашборда
        public Dictionary<string, int> PopularQueries { get; set; } = new Dictionary<string, int>();
        public int TodayRequests { get; set; }
        public int TodaySuccess { get; set; }
        public int TodayErrors { get; set; }
        public Dictionary<long, DashboardState> DashboardStates { get; set; } = new Dictionary<long, DashboardState>();

        [JsonIgnore]
        public object SyncRoot { get; } = new object();

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
            lock (SyncRoot)
            {
                try
                {
                    // Атомарная запись: пишем во временный файл, затем переименовываем поверх целевого.
                    // Так исключается побитый JSON и конфликт с другим пишущим потоком (IOException).
                    string tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, FilePath, overwrite: true);
                }
                catch { }
            }
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

    public class DashboardState
    {
        public int Page { get; set; } = 0;
        public string Filter { get; set; } = "all";
        public string Search { get; set; } = string.Empty;
        public bool AwaitingSearch { get; set; } = false;
    }
}