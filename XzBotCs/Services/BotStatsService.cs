using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using SkiaSharp;
using XzBotCs.Helpers;
using XzBotCs.Models;
using Telegram.Bot.Types.ReplyMarkups;

namespace XzBotCs.Services
{
    public class BotStatsService
    {
        private readonly BotState _state;

        public BotStatsService(BotState state)
        {
            _state = state;
        }

        public byte[] GenerateChartImage()
        {
            try
            {
                var dailyDict = _state.DailyUsage ?? new Dictionary<string, int>();

                // Если DailyUsage пустой, соберём данные из RecentRequests как запасной вариант.
                if (dailyDict.Count == 0 && _state.RecentRequests != null && _state.RecentRequests.Count > 0)
                {
                    dailyDict = _state.RecentRequests
                        .GroupBy(r => r.Time.ToString("dd.MM"))
                        .ToDictionary(g => g.Key, g => g.Count());
                }

                // Безопасно парсим ключи дат. Пропускаем некорректные ключи.
                var pointsList = new List<KeyValuePair<DateTime, int>>();
                foreach (var kv in dailyDict)
                {
                    if (DateTime.TryParseExact(kv.Key, "dd.MM", null, System.Globalization.DateTimeStyles.None, out var dt))
                    {
                        pointsList.Add(new KeyValuePair<DateTime, int>(dt, kv.Value));
                    }
                    else
                    {
                        if (DateTime.TryParse(kv.Key, out var dt2))
                        {
                            pointsList.Add(new KeyValuePair<DateTime, int>(dt2, kv.Value));
                        }
                        else
                        {
                            Console.WriteLine($"BotStats: пропущен ключ DailyUsage с некорректным форматом даты: '{kv.Key}'");
                        }
                    }
                }

                var sortedStats = pointsList.OrderBy(x => x.Key).Select(x => new KeyValuePair<string, int>(x.Key.ToString("dd.MM"), x.Value)).ToList();
                if (sortedStats.Count == 0)
                {
                    return CreatePlaceholderImage("Нет данных");
                }

                int width = 700;
                int height = 260;
                using var bitmap = new SKBitmap(width, height);
                using var canvas = new SKCanvas(bitmap);

                var bgColor = new SKColor(11, 18, 32);
                var gridColor = new SKColor(60, 75, 90, 160);
                var lineColor = new SKColor(33, 150, 243);
                var fillStart = new SKColor(33, 150, 243, 120);

                canvas.Clear(bgColor);

                int left = 60, right = 20, top = 20, bottom = 50;
                int plotWidth = width - left - right;
                int plotHeight = height - top - bottom;

                int maxVal = sortedStats.Max(x => x.Value);
                if (maxVal == 0) maxVal = 1;

                int yTicks = 4;
                using var gridPaint = new SKPaint { Color = gridColor, StrokeWidth = 1, IsAntialias = true };
                using var labelPaint = new SKPaint { Color = SKColors.LightGray, IsAntialias = true };
                using var labelFont = new SKFont(SKTypeface.Default, 12);
                for (int i = 0; i <= yTicks; i++)
                {
                    float yy = top + (plotHeight * i / (float)yTicks);
                    canvas.DrawLine(left, yy, left + plotWidth, yy, gridPaint);
                    int value = (int)Math.Round(maxVal * (1 - i / (float)yTicks));
                    canvas.DrawText(value.ToString(), 8, yy + 5, labelFont, labelPaint);
                }

                float xStep = plotWidth / (float)(sortedStats.Count > 1 ? sortedStats.Count - 1 : 1);
                var points = new List<SKPoint>();
                for (int i = 0; i < sortedStats.Count; i++)
                {
                    float x = left + i * xStep;
                    float y = top + (plotHeight - (sortedStats[i].Value / (float)maxVal * plotHeight));
                    points.Add(new SKPoint(x, y));
                }

                using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
                fillPaint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + plotHeight), new[] { fillStart, SKColors.Transparent }, null, SKShaderTileMode.Clamp);
                using var fillPath = new SKPath();
                fillPath.MoveTo(points[0].X, top + plotHeight);
                foreach (var p in points) fillPath.LineTo(p);
                fillPath.LineTo(points.Last().X, top + plotHeight);
                fillPath.Close();
                canvas.DrawPath(fillPath, fillPaint);

                using var shadowPaint = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(90), StrokeWidth = 8, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
                using var path = new SKPath();
                path.MoveTo(points[0]);
                for (int i = 1; i < points.Count; i++) path.LineTo(points[i]);
                canvas.DrawPath(path, shadowPaint);

                using var linePaint = new SKPaint { IsAntialias = true, Color = lineColor, StrokeWidth = 3, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
                canvas.DrawPath(path, linePaint);

                using var dotFill = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
                using var dotStroke = new SKPaint { IsAntialias = true, Color = lineColor, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
                using var haloPaint = new SKPaint { IsAntialias = true, Color = lineColor.WithAlpha(60), Style = SKPaintStyle.Fill };
                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    float r = (i == points.Count - 1) ? 5f : 3.5f;
                    canvas.DrawCircle(p.X, p.Y, r + 3, haloPaint);
                    canvas.DrawCircle(p.X, p.Y, r, dotFill);
                    canvas.DrawCircle(p.X, p.Y, r, dotStroke);
                }

                using var xLabelPaint = new SKPaint { Color = SKColors.LightGray, IsAntialias = true };
                using var xLabelFont = new SKFont(SKTypeface.Default, 12);
                int maxLabels = Math.Min(sortedStats.Count, 7);
                int step = Math.Max(1, sortedStats.Count / maxLabels);
                for (int i = 0; i < sortedStats.Count; i += step)
                {
                    var p = points[i];
                    string label = sortedStats[i].Key;
                    float textWidth = xLabelFont.MeasureText(label);
                    canvas.DrawText(label, p.X - textWidth / 2, top + plotHeight + 20, xLabelFont, xLabelPaint);
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BotStats: ошибка при генерации графика: {ex}");
                return CreatePlaceholderImage("Ошибка");
            }
        }

        public void IncrementUsage()
        {
            lock (_state.SyncRoot)
            {
                _state.UsageCount++;
                IncrementDailyUsage();
            }
        }

        private void IncrementDailyUsage()
        {
            string date = DateTime.Now.ToString("dd.MM");
            if (_state.DailyUsage.ContainsKey(date))
            {
                _state.DailyUsage[date]++;
            }
            else
            {
                _state.DailyUsage[date] = 1;
            }
        }

        public void RecordResponseTime(TimeSpan elapsed)
        {
            lock (_state.SyncRoot)
            {
                _state.ResponseTimesMs.Add(Math.Round(elapsed.TotalMilliseconds, 1));
                if (_state.ResponseTimesMs.Count > 1000)
                {
                    _state.ResponseTimesMs.RemoveRange(0, _state.ResponseTimesMs.Count - 1000);
                }
            }
        }

        /// <summary>Запись времени ответа конкретного провайдера (bing/ddg). Держим последние 1000 замеров на провайдера.</summary>
        public void RecordResponseTime(string providerKey, TimeSpan elapsed)
        {
            if (string.IsNullOrWhiteSpace(providerKey)) return;
            string key = providerKey.Trim().ToLowerInvariant();

            lock (_state.SyncRoot)
            {
                if (!_state.ResponseTimesByProvider.TryGetValue(key, out var list))
                {
                    list = new List<double>();
                    _state.ResponseTimesByProvider[key] = list;
                }

                list.Add(Math.Round(elapsed.TotalMilliseconds, 1));
                if (list.Count > 1000)
                {
                    list.RemoveRange(0, list.Count - 1000);
                }
            }
        }

        public void RecordError(string errorType)
        {
            lock (_state.SyncRoot)
            {
                _state.ErrorCount++;
                _state.ErrorDetails[errorType] = _state.ErrorDetails.TryGetValue(errorType, out int count) ? count + 1 : 1;

                // Ограничиваем размер словаря: оставляем 50 самых частых типов ошибок.
                if (_state.ErrorDetails.Count > 50)
                {
                    var top = _state.ErrorDetails
                        .OrderByDescending(kv => kv.Value)
                        .Take(50)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    _state.ErrorDetails.Clear();
                    foreach (var kv in top) _state.ErrorDetails[kv.Key] = kv.Value;
                }
            }
        }

        public void RecordRequest(long userId, string? username, string query, bool success)
        {
            lock (_state.SyncRoot)
            {
                var now = DateTime.Now;
                _state.RecentRequests.Insert(0, new RequestRecord
                {
                    Time = now,
                    UserId = userId,
                    Username = string.IsNullOrWhiteSpace(username) ? "Unknown" : username,
                    Query = query,
                    Success = success
                });

                if (_state.RecentRequests.Count > 200)
                {
                    _state.RecentRequests.RemoveRange(200, _state.RecentRequests.Count - 200);
                }

                UpdateStatsIncremental(now, query, success);
            }

            ScheduleSave();
        }

        private static readonly object _saveLock = new object();
        private static DateTime _lastSaveUtc = DateTime.MinValue;
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Сохраняет состояние не чаще раза в <see cref="SaveInterval"/>.
        /// При остановке бота Main вызывает _state.Save(), так что данные не теряются.
        /// </summary>
        private void ScheduleSave()
        {
            lock (_saveLock)
            {
                if (DateTime.UtcNow - _lastSaveUtc < SaveInterval)
                {
                    return;
                }

                _lastSaveUtc = DateTime.UtcNow;
            }

            _state.Save();
        }

        public string BuildStatsText(IEnumerable<(string Key, string DisplayName, bool Ok, string Status)> providerStatuses)
        {
            var uptime = DateTime.Now - _state.StartedAt;
            string uptimeStr = FormatUptime(uptime);
            string startedAt = _state.StartedAt.ToString("dd.MM.yyyy HH:mm:ss");
            int successCount = Math.Max(0, _state.UsageCount - _state.ErrorCount);
            double successRate = _state.UsageCount > 0
                ? Math.Round(successCount / (double)_state.UsageCount * 100, 1)
                : 100.0;

            var servicesLines = new System.Text.StringBuilder();
            foreach (var p in providerStatuses)
            {
                string icon = p.Ok ? "✅" : "❌";
                servicesLines.Append($"  {TelegramEscaper.EscapeMarkdownV2(p.DisplayName)}: {icon} `{TelegramEscaper.EscapeMarkdownV2(p.Status)}`\n");
            }

            return "📊 *Статистика бота*\n\n" +
                   "⏱ *Аптайм*\n" +
                   $"  `{TelegramEscaper.EscapeMarkdownV2(uptimeStr)}` \\(с `{TelegramEscaper.EscapeMarkdownV2(startedAt)}`\\)\n\n" +
                   "🌐 *Внешние сервисы*\n" +
                   servicesLines +
                   "\n📈 *Запросы*\n" +
                   $"  Всего: `{_state.UsageCount}`\n" +
                   $"  Успешных: `{successCount}` \\({TelegramEscaper.EscapeMarkdownV2(successRate.ToString())}%\\)\n" +
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

                    // Время ответа отдельным блоком для каждого провайдера (Bing, DuckDuckGo).
                    // Показываем оба всегда, даже если замеров ещё нет.
                    string[] providerOrder = { "bing", "ddg" };
                    foreach (var providerKey in providerOrder)
                    {
                        string providerName = providerKey == "ddg" ? "DuckDuckGo" : "Bing";

                        List<double> times;
                        lock (_state.SyncRoot)
                        {
                            _state.ResponseTimesByProvider.TryGetValue(providerKey, out times!);
                        }

                        if (times == null || times.Count == 0)
                        {
                            lines.Add($"⏱ *Время ответа {providerName}:* нет данных");
                            lines.Add("");
                            continue;
                        }

                        lines.Add($"⏱ *Время ответа {providerName}*");
                        lines.Add($"  среднее:  `{TelegramEscaper.EscapeMarkdownV2(Math.Round(times.Average(), 1).ToString())} мс`");
                        lines.Add($"  мин:      `{TelegramEscaper.EscapeMarkdownV2(times.Min().ToString())} мс`");
                        lines.Add($"  макс:     `{TelegramEscaper.EscapeMarkdownV2(times.Max().ToString())} мс`");
                        lines.Add($"  замеров:  `{times.Count}`");
                        lines.Add("");
                    }

                    lines.Add($"🔢 *Нагрузка:* `{TelegramEscaper.EscapeMarkdownV2(requestsPerMinute.ToString())}` зап/мин");
                    lines.Add("");

                    if (_state.ErrorDetails.Count > 0)
                    {
                        lines.Add("⚠️ *Ошибки по типам:*");
                        foreach (var item in _state.ErrorDetails.OrderByDescending(x => x.Value))
                        {
                            lines.Add($"  `{TelegramEscaper.EscapeMarkdownV2(item.Key)}` — `{item.Value}`");
                        }
                    }
                    else
                    {
                        lines.Add("✅ *Ошибок не зафиксировано*");
                    }

                    return string.Join("\n", lines);
                }

        public string BuildDashboardText(long userId, int? page = null, string? filter = null, string? search = null)
        {
            if (!_state.DashboardStates.ContainsKey(userId))
            {
                _state.DashboardStates[userId] = new DashboardState();
            }
            var state = _state.DashboardStates[userId];

            if (page.HasValue) state.Page = page.Value;
            if (filter != null) state.Filter = filter;
            if (search != null) state.Search = search;

            var lines = new List<string>
            {
                "📋 *Дашборд последних запросов*",
                ""
            };

            var today = DateTime.Today;
            var todayRequests = _state.RecentRequests.Where(r => r.Time.Date == today).ToList();
            int todayTotal = todayRequests.Count;
            int todaySuccess = todayRequests.Count(r => r.Success);
            int todayErrors = todayRequests.Count(r => !r.Success);

            var topQueries = todayRequests
                .GroupBy(r => r.Query)
                .Select(g => new { Query = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            lines.Add($"📊 *За сегодня:* `{todayTotal}` запросов ✅ `{todaySuccess}` ❌ `{todayErrors}`");
            if (topQueries.Count > 0)
            {
                string topStr = string.Join(", ", topQueries.Select(x => $"\"{TelegramEscaper.EscapeMarkdownV2(x.Query.Trim())}\" \\({x.Count}\\)"));
                lines.Add($"🔥 *Топ:* {topStr}");
            }
            lines.Add("");

            var filtered = _state.RecentRequests.AsEnumerable();
            if (state.Filter == "success")
                filtered = filtered.Where(r => r.Success);
            else if (state.Filter == "errors")
                filtered = filtered.Where(r => !r.Success);

            if (!string.IsNullOrEmpty(state.Search))
            {
                filtered = filtered.Where(r => r.Query.Contains(state.Search, StringComparison.OrdinalIgnoreCase));
            }

            var list = filtered.ToList();
            if (list.Count == 0)
            {
                lines.Add("  _Нет записей по текущему фильтру_");
                lines.Add("");
                lines.Add($"Страница {state.Page + 1}/1");
                return string.Join("\n", lines);
            }

            int pageSize = 10;
            int totalPages = (int)Math.Ceiling(list.Count / (double)pageSize);
            if (state.Page >= totalPages) state.Page = totalPages - 1;
            if (state.Page < 0) state.Page = 0;

            var paged = list.Skip(state.Page * pageSize).Take(pageSize).ToList();

            lines.Add($"📄 *Страница {state.Page + 1}/{totalPages}* \\(всего {list.Count} записей\\)");
            lines.Add("");

            foreach (var request in paged)
            {
                string time = request.Time.ToString("HH:mm:ss");
                string status = request.Success ? "✅" : "❌";
                string query = request.Query.Length > 25 ? request.Query.Substring(0, 25) + "..." : request.Query;
                lines.Add($"`[{TelegramEscaper.EscapeMarkdownV2(time)}]` {status} `@{TelegramEscaper.EscapeMarkdownV2(request.Username)}` \\(`{request.UserId}`\\): _{TelegramEscaper.EscapeMarkdownV2(query)}_");
            }

            return string.Join("\n", lines);
        }

        public InlineKeyboardMarkup BuildDashboardMarkup(long userId)
        {
            if (!_state.DashboardStates.ContainsKey(userId))
            {
                _state.DashboardStates[userId] = new DashboardState();
            }
            var state = _state.DashboardStates[userId];

            var buttons = new List<List<InlineKeyboardButton>>();

            var navButtons = new List<InlineKeyboardButton>();
            navButtons.Add(InlineKeyboardButton.WithCallbackData("◀️ Назад", $"dash:page:{state.Page - 1}"));
            navButtons.Add(InlineKeyboardButton.WithCallbackData($"{state.Page + 1}", $"dash:page:{state.Page}"));
            navButtons.Add(InlineKeyboardButton.WithCallbackData("Вперед ▶️", $"dash:page:{state.Page + 1}"));
            buttons.Add(navButtons);

            var filterButtons = new List<InlineKeyboardButton>();
            string allLabel = state.Filter == "all" || state.Filter == null ? "✅ Все" : "Все";
            string successLabel = state.Filter == "success" ? "✅ Успешные" : "Успешные";
            string errorsLabel = state.Filter == "errors" ? "✅ Ошибки" : "Ошибки";
            filterButtons.Add(InlineKeyboardButton.WithCallbackData(allLabel, "dash:filter:all"));
            filterButtons.Add(InlineKeyboardButton.WithCallbackData(successLabel, "dash:filter:success"));
            filterButtons.Add(InlineKeyboardButton.WithCallbackData(errorsLabel, "dash:filter:errors"));
            buttons.Add(filterButtons);

            var extraButtons = new List<InlineKeyboardButton>();
            extraButtons.Add(InlineKeyboardButton.WithCallbackData("🔄 Обновить", "dash:refresh"));
            if (!string.IsNullOrEmpty(state.Search))
            {
                extraButtons.Add(InlineKeyboardButton.WithCallbackData("🔍 Сброс поиска", "dash:search:clear"));
            }
            else
            {
                extraButtons.Add(InlineKeyboardButton.WithCallbackData("🔍 Поиск", "dash:search"));
            }
            buttons.Add(extraButtons);

            return new InlineKeyboardMarkup(buttons);
        }

        public void SetDashboardAwaitingSearch(long userId, bool awaiting)
        {
            if (!_state.DashboardStates.ContainsKey(userId))
                _state.DashboardStates[userId] = new DashboardState();
            _state.DashboardStates[userId].AwaitingSearch = awaiting;
            _state.Save();
        }

        private DateTime _statsDay = DateTime.MinValue;

        /// <summary>
        /// Инкрементально обновляет дневную статистику и популярные запросы.
        /// Полный пересчёт из RecentRequests выполняется только при смене дня.
        /// </summary>
        private void UpdateStatsIncremental(DateTime now, string query, bool success)
        {
            if (_statsDay != now.Date)
            {
                // Смена дня — считаем стартовые значения один раз.
                _statsDay = now.Date;
                var todayRequests = _state.RecentRequests.Where(r => r.Time.Date == now.Date).ToList();
                _state.TodayRequests = todayRequests.Count;
                _state.TodaySuccess = todayRequests.Count(r => r.Success);
                _state.TodayErrors = todayRequests.Count(r => !r.Success);
            }
            else
            {
                _state.TodayRequests++;
                if (success) _state.TodaySuccess++;
                else _state.TodayErrors++;
            }

            if (!string.IsNullOrEmpty(query))
            {
                _state.PopularQueries[query] = _state.PopularQueries.TryGetValue(query, out int c) ? c + 1 : 1;

                if (_state.PopularQueries.Count > 50)
                {
                    var top = _state.PopularQueries
                        .OrderByDescending(kv => kv.Value)
                        .Take(50)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    _state.PopularQueries.Clear();
                    foreach (var kv in top) _state.PopularQueries[kv.Key] = kv.Value;
                }
            }
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

        private byte[] CreatePlaceholderImage(string text)
        {
            int width = 400;
            int height = 120;
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            var bg = new SKColor(24, 32, 48);
            canvas.Clear(bg);

            using var paint = new SKPaint
            {
                Color = SKColors.LightGray,
                IsAntialias = true,
                TextSize = 20,
                Typeface = SKTypeface.Default
            };

            var textWidth = paint.MeasureText(text);
            canvas.DrawText(text, (width - textWidth) / 2, height / 2 + paint.TextSize / 2, paint);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
    }
}