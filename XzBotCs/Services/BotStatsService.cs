using System;
using System.Collections.Generic;
using System.Linq;
using XzBotCs.Models;

namespace XzBotCs.Services
{
    public class BotStatsService
    {
        private readonly BotState _state;

        public BotStatsService(BotState state)
        {
            _state = state;
        }

        public void IncrementUsage()
        {
            _state.UsageCount++;
        }

        public void RecordResponseTime(TimeSpan elapsed)
        {
            _state.ResponseTimesMs.Add(Math.Round(elapsed.TotalMilliseconds, 1));
            if (_state.ResponseTimesMs.Count > 1000)
            {
                _state.ResponseTimesMs.RemoveRange(0, _state.ResponseTimesMs.Count - 1000);
            }
        }

        public void RecordError(string errorType)
        {
            _state.ErrorCount++;
            _state.ErrorDetails[errorType] = _state.ErrorDetails.TryGetValue(errorType, out int count) ? count + 1 : 1;
        }

        public void RecordRequest(long userId, string? username, string query, bool success)
        {
            _state.RecentRequests.Insert(0, new RequestRecord
            {
                Time = DateTime.Now,
                UserId = userId,
                Username = string.IsNullOrWhiteSpace(username) ? "Unknown" : username,
                Query = query,
                Success = success
            });

            if (_state.RecentRequests.Count > 20)
            {
                _state.RecentRequests.RemoveRange(20, _state.RecentRequests.Count - 20);
            }
        }

        public string BuildStatsText(bool bingOk, string bingStatus)
        {
            var uptime = DateTime.Now - _state.StartedAt;
            string uptimeStr = FormatUptime(uptime);
            string startedAt = _state.StartedAt.ToString("dd.MM.yyyy HH:mm:ss");
            string bingIcon = bingOk ? "✅" : "❌";
            int successCount = Math.Max(0, _state.UsageCount - _state.ErrorCount);
            double successRate = _state.UsageCount > 0
                ? Math.Round(successCount / (double)_state.UsageCount * 100, 1)
                : 100.0;

            return "📊 *Статистика бота*\n\n" +
                   "⏱ *Аптайм*\n" +
                   $"  `{Escape(uptimeStr)}` \\(с `{Escape(startedAt)}`\\)\n\n" +
                   "🌐 *Внешние сервисы*\n" +
                   $"  Bing: {bingIcon} `{Escape(bingStatus)}`\n\n" +
                   "📈 *Запросы*\n" +
                   $"  Всего: `{_state.UsageCount}`\n" +
                   $"  Успешных: `{successCount}` \\({Escape(successRate.ToString())}%\\)\n" +
                   $"  Ошибок: `{_state.ErrorCount}`\n\n" +
                   "🔐 _admin only_";
        }

        public string BuildMetricsText()
        {
            var uptime = DateTime.Now - _state.StartedAt;
            double requestsPerMinute = uptime.TotalSeconds > 0
                ? Math.Round(_state.UsageCount / (uptime.TotalSeconds / 60), 2)
                : 0;

            var lines = new List<string>
            {
                "📈 *Метрики производительности*",
                ""
            };

            if (_state.ResponseTimesMs.Count > 0)
            {
                lines.Add("⏱ *Время ответа Bing*");
                lines.Add($"  среднее:  `{Escape(Math.Round(_state.ResponseTimesMs.Average(), 1).ToString())} мс`");
                lines.Add($"  мин:      `{Escape(_state.ResponseTimesMs.Min().ToString())} мс`");
                lines.Add($"  макс:     `{Escape(_state.ResponseTimesMs.Max().ToString())} мс`");
                lines.Add($"  замеров:  `{_state.ResponseTimesMs.Count}`");
            }
            else
            {
                lines.Add("⏱ *Время ответа Bing:* нет данных");
            }

            lines.Add("");
            lines.Add($"🔢 *Нагрузка:* `{Escape(requestsPerMinute.ToString())}` зап/мин");
            lines.Add("");

            if (_state.ErrorDetails.Count > 0)
            {
                lines.Add("⚠️ *Ошибки по типам:*");
                foreach (var item in _state.ErrorDetails.OrderByDescending(x => x.Value))
                {
                    lines.Add($"  `{Escape(item.Key)}` — `{item.Value}`");
                }
            }
            else
            {
                lines.Add("✅ *Ошибок не зафиксировано*");
            }

            return string.Join("\n", lines);
        }

        public string BuildDashboardText()
        {
            var lines = new List<string>
            {
                "📋 *Дашборд последних запросов*",
                ""
            };

            if (_state.RecentRequests.Count == 0)
            {
                lines.Add("  _Пусто_");
                return string.Join("\n", lines);
            }

            foreach (var request in _state.RecentRequests.Take(10))
            {
                string time = request.Time.ToString("HH:mm:ss");
                string status = request.Success ? "✅" : "❌";
                string query = request.Query.Length > 20 ? request.Query.Substring(0, 20) + "..." : request.Query;
                lines.Add($"`[{Escape(time)}]` {status} `@{Escape(request.Username)}` \\(`{request.UserId}`\\): _{Escape(query)}_");
            }

            return string.Join("\n", lines);
        }

        private static string FormatUptime(TimeSpan uptime)
        {
            var parts = new List<string>();
            if (uptime.Days > 0) parts.Add($"{uptime.Days}д");
            if (uptime.Hours > 0) parts.Add($"{uptime.Hours}ч");
            if (uptime.Minutes > 0) parts.Add($"{uptime.Minutes}м");
            parts.Add($"{uptime.Seconds}с");
            return string.Join(" ", parts);
        }

        private static string Escape(string text)
        {
            return text.Replace("\\", "\\\\")
                .Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[")
                .Replace("]", "\\]").Replace("(", "\\(").Replace(")", "\\)")
                .Replace("~", "\\~").Replace("`", "\\`").Replace(">", "\\>")
                .Replace("#", "\\#").Replace("+", "\\+").Replace("-", "\\-")
                .Replace("=", "\\=").Replace("|", "\\|").Replace("{", "\\{")
                .Replace("}", "\\}").Replace(".", "\\.").Replace("!", "\\!");
        }
    }
}
