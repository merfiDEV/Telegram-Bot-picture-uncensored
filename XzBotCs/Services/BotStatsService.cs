using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using SkiaSharp;
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

        public byte[] GenerateChartImage()
        {
            var dailyDict = _state.DailyUsage ?? new Dictionary<string, int>();

            // Если DailyUsage пустой, соберём данные из RecentRequests как запасной вариант.
            if (dailyDict.Count == 0 && _state.RecentRequests != null && _state.RecentRequests.Count > 0)
            {
                dailyDict = _state.RecentRequests
                    .GroupBy(r => r.Time.ToString("dd.MM"))
                    .ToDictionary(g => g.Key, g => g.Count());
            }

            var sortedStats = dailyDict.OrderBy(x => DateTime.ParseExact(x.Key, "dd.MM", null)).ToList();
            if (sortedStats.Count == 0) return new byte[0];

            int width = 700;
            int height = 260;
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);

            // Цвета и отступы
            var bgColor = new SKColor(11, 18, 32);
            var gridColor = new SKColor(60, 75, 90, 160);
            var axisColor = new SKColor(80, 100, 120);
            var lineColor = new SKColor(33, 150, 243);
            var fillStart = new SKColor(33, 150, 243, 120);

            canvas.Clear(bgColor);

            int left = 60, right = 20, top = 20, bottom = 50;
            int plotWidth = width - left - right;
            int plotHeight = height - top - bottom;

            int maxVal = sortedStats.Max(x => x.Value);
            if (maxVal == 0) maxVal = 1;

            // Горизонтальные сетки и подписи Y
            int yTicks = 4;
            using var gridPaint = new SKPaint { Color = gridColor, StrokeWidth = 1, IsAntialias = true };
            using var labelPaint = new SKPaint { Color = SKColors.LightGray, IsAntialias = true };
            using var labelFont = new SKFont(SKTypeface.Default, 12);
            for (int i = 0; i <= yTicks; i++)
            {
                float yy = top + (plotHeight * i / (float)yTicks);
                canvas.DrawLine(left, yy, left + plotWidth, yy, gridPaint);
                int value = (int)Math.Round(maxVal * (1 - i / (float)yTicks));
                var label = value.ToString();
                canvas.DrawText(label, 8, yy + 5, labelFont, labelPaint);
            }

            // Точки данных
            float xStep = plotWidth / (float)(sortedStats.Count > 1 ? sortedStats.Count - 1 : 1);
            var points = new List<SKPoint>();
            for (int i = 0; i < sortedStats.Count; i++)
            {
                float x = left + i * xStep;
                float y = top + (plotHeight - (sortedStats[i].Value / (float)maxVal * plotHeight));
                points.Add(new SKPoint(x, y));
            }

            // Заливка под линией
            using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            fillPaint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + plotHeight), new[] { fillStart, SKColors.Transparent }, null, SKShaderTileMode.Clamp);
            using var fillPath = new SKPath();
            fillPath.MoveTo(points[0].X, top + plotHeight);
            foreach (var p in points) fillPath.LineTo(p);
            fillPath.LineTo(points.Last().X, top + plotHeight);
            fillPath.Close();
            canvas.DrawPath(fillPath, fillPaint);

            // Тень линии
            using var shadowPaint = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(90), StrokeWidth = 8, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
            using var path = new SKPath();
            path.MoveTo(points[0]);
            for (int i = 1; i < points.Count; i++) path.LineTo(points[i]);
            canvas.DrawPath(path, shadowPaint);

            // Основная линия
            using var linePaint = new SKPaint { IsAntialias = true, Color = lineColor, StrokeWidth = 3, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
            canvas.DrawPath(path, linePaint);

            // Маркеры точек
            using var dotFill = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
            using var dotStroke = new SKPaint { IsAntialias = true, Color = lineColor, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
            using var haloPaint = new SKPaint { IsAntialias = true, Color = lineColor.WithAlpha(60), Style = SKPaintStyle.Fill };
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                float r = (i == points.Count - 1) ? 5f : 3.5f;
                // ореол
                canvas.DrawCircle(p.X, p.Y, r + 3, haloPaint);
                canvas.DrawCircle(p.X, p.Y, r, dotFill);
                canvas.DrawCircle(p.X, p.Y, r, dotStroke);
            }

            // Подписи X (через равные интервалы, чтобы не налезали)
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
// ... остальной код (методы IncrementUsage, RecordResponseTime и т.д.)
// (Мне нужно вставить весь класс целиком, чтобы не терять методы. Но я могу просто добавить метод.)


        public void IncrementUsage()
        {
            _state.UsageCount++;
            IncrementDailyUsage();
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
