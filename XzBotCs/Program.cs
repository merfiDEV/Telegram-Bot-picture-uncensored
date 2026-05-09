using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using XzBotCs.Models;
using XzBotCs.Services;

namespace XzBotCs
{
    class Program
    {
        private static ITelegramBotClient? _botClient;
        private static BotState _state = BotState.Load();
        private static BingSearchService _searchService = new BingSearchService();
        private static WatermarkService _watermarkService = new WatermarkService();
        private static BotStatsService _statsService = new BotStatsService(_state);
        private static HttpClient _httpClient = new HttpClient();
        private static long? _adminId;
        private static string? _proxyBaseUrl;

        private const int DefaultProxyPort = 8080;
        private const string DeveloperProfileUrl = "https://t.me/Tyta_Zdesyaa777";

        static async Task Main(string[] args)
        {
            string? token = Environment.GetEnvironmentVariable("BOT_TOKEN");
            string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_ID");
            string? proxyBaseUrl = Environment.GetEnvironmentVariable("PROXY_BASE_URL")
                ?? Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
            string? proxyPortStr = Environment.GetEnvironmentVariable("PROXY_PORT");

            if (System.IO.File.Exists("../.env"))
            {
                var lines = System.IO.File.ReadAllLines("../.env");
                foreach (var line in lines)
                {
                    if (line.StartsWith("BOT_TOKEN=")) token = ReadEnvValue(line, "BOT_TOKEN=");
                    if (line.StartsWith("ADMIN_ID=")) adminIdStr = ReadEnvValue(line, "ADMIN_ID=");
                    if (line.StartsWith("PROXY_BASE_URL=")) proxyBaseUrl = ReadEnvValue(line, "PROXY_BASE_URL=");
                    if (line.StartsWith("PUBLIC_BASE_URL=")) proxyBaseUrl = ReadEnvValue(line, "PUBLIC_BASE_URL=");
                    if (line.StartsWith("PROXY_PORT=")) proxyPortStr = ReadEnvValue(line, "PROXY_PORT=");
                }
            }

            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine("Error: BOT_TOKEN not found.");
                return;
            }

            if (long.TryParse(adminIdStr, out long aid)) _adminId = aid;
            int proxyPort = int.TryParse(proxyPortStr, out int parsedProxyPort) ? parsedProxyPort : DefaultProxyPort;
            _proxyBaseUrl = NormalizeProxyBaseUrl(proxyBaseUrl);
            SetupLogging();

            _botClient = new TelegramBotClient(token);

            using var cts = new CancellationTokenSource();

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token
            );

            _ = Task.Run(() => StartProxyAsync(proxyPort, cts.Token));

            var me = await _botClient.GetMe(cts.Token);
            Console.WriteLine($"Start listening for @{me.Username}");
            Console.WriteLine(string.IsNullOrEmpty(_proxyBaseUrl)
                ? "Watermark proxy URL is not configured. Set PROXY_BASE_URL, for example: https://example.com/img?u="
                : $"Watermark proxy URL: {_proxyBaseUrl}");
            Console.WriteLine("Press any key to exit");
            
            // Keep app running
            while (!cts.IsCancellationRequested)
            {
                if (Console.KeyAvailable) break;
                await Task.Delay(100);
            }

            cts.Cancel();
            _state.Save();
        }

        private static string Escape(string text)
        {
            return text.Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[").Replace("]", "\\]").Replace("(", "\\(").Replace(")", "\\)").Replace("~", "\\~").Replace("`", "\\`").Replace(">", "\\>").Replace("#", "\\#").Replace("+", "\\+").Replace("-", "\\-").Replace("=", "\\=").Replace("|", "\\|").Replace("{", "\\{").Replace("}", "\\}").Replace(".", "\\.").Replace("!", "\\!");
        }

        private static string ReadEnvValue(string line, string key)
        {
            return line.Substring(key.Length).Trim().Trim('"').Trim('\'');
        }

        private static void SetupLogging()
        {
            string logsDir = Path.Combine("..", "logs");
            Directory.CreateDirectory(logsDir);

            string logPath = Path.Combine(logsDir, $"logs_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var fileWriter = TextWriter.Synchronized(new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true });
            var consoleOut = Console.Out;
            var consoleErr = Console.Error;
            var writer = TextWriter.Synchronized(new TeeTextWriter(consoleOut, fileWriter));

            Console.SetOut(writer);
            Console.SetError(TextWriter.Synchronized(new TeeTextWriter(consoleErr, fileWriter)));
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | INFO | logging started: {logPath}");
        }

        private sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter _first;
            private readonly TextWriter _second;

            public TeeTextWriter(TextWriter first, TextWriter second)
            {
                _first = first;
                _second = second;
            }

            public override Encoding Encoding => _first.Encoding;

            public override void WriteLine(string? value)
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {value}";
                _first.WriteLine(line);
                _second.WriteLine(line);
            }

            public override void Write(char value)
            {
                _first.Write(value);
                _second.Write(value);
            }
        }

        static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is { } message && message.Text is { } messageText)
            {
                if (messageText.StartsWith("/start"))
                {
                    if (messageText.Contains("developer"))
                    {
                        var devBtn = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("💻 Открыть профиль", DeveloperProfileUrl));
                        await botClient.SendMessage(message.Chat.Id, "💻 *Профиль разработчика*", parseMode: ParseMode.Markdown, replyMarkup: devBtn, cancellationToken: cancellationToken);
                        return;
                    }

                    string text = "*🤖 Бот работает в асинхронном inline режиме!*\n\n" +
                                 "Чтобы использовать бота, откройте любой чат и введите:\n" +
                                 "`@имя_бота ваш_запрос`\n\n" +
                                 "⚡ *Новинка:* Используйте флаг `--gif` в конце запроса для поиска анимаций.\n\n" +
                                 "⚠️ *Дисклеймер*\n" +
                                 "Данный бот автоматически обрабатывает поисковые запросы пользователей и " +
                                 "показывает результаты из *открытых источников* в интернете.\n\n" +
                                 "*Важные правила:*\n" +
                                 "— Создатель не хранит и не модерирует контент\n" +
                                 "— Вся ответственность за запросы лежит на пользователе\n" +
                                 "— Используя бота, вы подтверждаете соблюдение законов вашей страны";
                    
                    var builder = new InlineKeyboardMarkup(InlineKeyboardButton.WithSwitchInlineQueryCurrentChat("🔍 Попробовать поиск", ""));
                    await botClient.SendMessage(message.Chat.Id, text, parseMode: ParseMode.Markdown, replyMarkup: builder, cancellationToken: cancellationToken);
                }
                else if (messageText == "/stats")
                {
                    if (_adminId == null || message.From?.Id != _adminId)
                    {
                        await botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                        return;
                    }
                    await SendStatsAsync(message.Chat.Id, cancellationToken);
                }
                else if (messageText == "/logs")
                {
                    if (message.Chat.Type != ChatType.Private)
                    {
                        await botClient.SendMessage(message.Chat.Id, "Доступно только в ЛС", cancellationToken: cancellationToken);
                        return;
                    }

                    if (_adminId == null || message.From?.Id != _adminId)
                    {
                        await botClient.SendMessage(message.Chat.Id, "Нет доступа :/", cancellationToken: cancellationToken);
                        return;
                    }

                    string logsPath = "../logs";
                    if (!Directory.Exists(logsPath)) Directory.CreateDirectory(logsPath);

                    string zipPath = Path.Combine(Path.GetTempPath(), $"logs_{Guid.NewGuid():N}.zip");
                    try
                    {
                        System.IO.Compression.ZipFile.CreateFromDirectory(logsPath, zipPath);
                        using var stream = System.IO.File.OpenRead(zipPath);
                        await botClient.SendDocument(message.Chat.Id, InputFile.FromStream(stream, "logs.zip"), cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        await botClient.SendMessage(message.Chat.Id, $"Ошибка при сборе логов: {Escape(ex.Message)}", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                    }
                    finally
                    {
                        if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
                    }
                }
            }
            else if (update.CallbackQuery is { } callbackQuery)
            {
                if (_adminId == null || callbackQuery.From.Id != _adminId)
                {
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, "⛔ Нет доступа", showAlert: true, cancellationToken: cancellationToken);
                    return;
                }

                if (callbackQuery.Data == "toggle_wm")
                {
                    _state.IsWatermarkEnabled = !_state.IsWatermarkEnabled;
                    _state.Save();
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, $"Ватермарка: {(_state.IsWatermarkEnabled ? "ВКЛ" : "ВЫКЛ")}", cancellationToken: cancellationToken);
                    await RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
                }
                else if (callbackQuery.Data == "stats:refresh")
                {
                    await RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, "Обновлено ✅", cancellationToken: cancellationToken);
                }
                else if (callbackQuery.Data == "stats:back")
                {
                    await RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                }
                else if (callbackQuery.Data == "stats:metrics")
                {
                    string text = _statsService.BuildMetricsText();
                    var markup = new InlineKeyboardMarkup(new[] { 
                        new [] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "stats:back") },
                        new [] { InlineKeyboardButton.WithCallbackData("🔄 Обновить", "stats:metrics") }
                    });
                    await botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: cancellationToken);
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                }
                else if (callbackQuery.Data == "stats:dashboard")
                {
                    string text = _statsService.BuildDashboardText();
                    var markup = new InlineKeyboardMarkup(new[] {
                        new [] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "stats:back") },
                        new [] { InlineKeyboardButton.WithCallbackData("🔄 Обновить", "stats:dashboard") }
                    });
                    await botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: cancellationToken);
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                }
            }
            else if (update.InlineQuery is { } inlineQuery)
            {
                string query = inlineQuery.Query.Trim();
                if (string.IsNullOrEmpty(query))
                {
                    await AnswerEmptyInlineQuery(botClient, inlineQuery.Id, cancellationToken);
                    return;
                }

                int offset = int.TryParse(inlineQuery.Offset, out int parsedOffset) ? parsedOffset : 0;
                Console.WriteLine($"Inline query from {inlineQuery.From.Id}: '{query}', offset={offset}");
                _statsService.IncrementUsage();
                var searchResponse = await _searchService.SearchImagesDetailedAsync(query, startIndex: offset + 1, limit: 30);
                if (searchResponse.ResponseTime > TimeSpan.Zero)
                {
                    _statsService.RecordResponseTime(searchResponse.ResponseTime);
                }
                if (!string.IsNullOrEmpty(searchResponse.ErrorType))
                {
                    _statsService.RecordError(searchResponse.ErrorType);
                }

                var searchResults = searchResponse.Items;
                Console.WriteLine($"Search returned {searchResults.Count} results for '{query}'");

                var results = new List<InlineQueryResult>();
                foreach (var item in searchResults)
                {
                    string finalUrl = item.Url;
                    string thumbnailUrl = string.IsNullOrEmpty(item.ThumbnailUrl) ? item.Url : item.ThumbnailUrl;
                    if (_state.IsWatermarkEnabled && !item.IsGif)
                    {
                        finalUrl = BuildProxyImageUrl(item.Url);
                    }

                    if (item.IsGif)
                    {
                        results.Add(new InlineQueryResultGif(item.Id, finalUrl, thumbnailUrl)
                        {
                            ReplyMarkup = BuildSourceMarkup(item)
                        });
                    }
                    else
                    {
                        results.Add(new InlineQueryResultPhoto(item.Id, finalUrl, thumbnailUrl)
                        {
                            ReplyMarkup = BuildSourceMarkup(item)
                        });
                    }
                }

                string nextOffset = searchResponse.ConsumedCount > 0 ? (offset + searchResponse.ConsumedCount).ToString() : "";
                await botClient.AnswerInlineQuery(
                    inlineQuery.Id,
                    results,
                    cacheTime: 300,
                    isPersonal: false,
                    nextOffset: nextOffset,
                    button: BuildDeveloperInlineButton(),
                    cancellationToken: cancellationToken);

                _statsService.RecordRequest(inlineQuery.From.Id, inlineQuery.From.Username, query, searchResults.Count > 0 && string.IsNullOrEmpty(searchResponse.ErrorType));
                _state.Save();
            }
        }

        static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine(exception);
            return Task.CompletedTask;
        }

        static async Task SendStatsAsync(long chatId, CancellationToken ct)
        {
            var (bingOk, bingStatus) = await _searchService.CheckBingAsync();
            var text = _statsService.BuildStatsText(bingOk, bingStatus);
            var markup = BuildStatsMarkup();
            await _botClient!.SendMessage(chatId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
        }

        static string BuildStatsText()
        {
            return _statsService.BuildStatsText(false, "checking");
        }

        static async Task RefreshStatsAsync(long chatId, int messageId, CancellationToken ct)
        {
            var (bingOk, bingStatus) = await _searchService.CheckBingAsync();
            var text = _statsService.BuildStatsText(bingOk, bingStatus);
            var markup = BuildStatsMarkup();
            try
            {
                await _botClient!.EditMessageText(chatId, messageId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
            }
            catch { }
        }

        static InlineKeyboardMarkup BuildStatsMarkup()
        {
            string wmBtnText = _state.IsWatermarkEnabled ? "❌ Выключить ватермарку" : "✅ Включить ватермарку";
            return new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("📈 Метрики", "stats:metrics"), InlineKeyboardButton.WithCallbackData("📋 Дашборд", "stats:dashboard") },
                new [] { InlineKeyboardButton.WithCallbackData(wmBtnText, "toggle_wm") },
                new [] { InlineKeyboardButton.WithCallbackData("🔄 Обновить", "stats:refresh") }
            });
        }

        private static string? NormalizeProxyBaseUrl(string? proxyBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(proxyBaseUrl)) return null;

            proxyBaseUrl = proxyBaseUrl.Trim();
            return proxyBaseUrl.Contains("?u=", StringComparison.OrdinalIgnoreCase)
                ? proxyBaseUrl
                : $"{proxyBaseUrl.TrimEnd('/')}/img?u=";
        }

        private static string BuildProxyImageUrl(string imageUrl)
        {
            if (string.IsNullOrEmpty(_proxyBaseUrl))
            {
                return imageUrl;
            }

            string b64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(imageUrl));
            return $"{_proxyBaseUrl}{WebUtility.UrlEncode(b64Url)}";
        }

        private static InlineQueryResultsButton BuildDeveloperInlineButton()
        {
            return new InlineQueryResultsButton("💻 Профиль разработчика >")
            {
                StartParameter = "developer"
            };
        }

        private static async Task AnswerEmptyInlineQuery(ITelegramBotClient botClient, string inlineQueryId, CancellationToken cancellationToken)
        {
            var emptyResult = new InlineQueryResultArticle(
                "empty-query",
                "🔍 Введите запрос",
                new InputTextMessageContent("Введите запрос после имени бота, и я найду картинки."))
            {
                Description = "Напишите, какую картинку найти. Например: кот в очках"
            };

            await botClient.AnswerInlineQuery(
                inlineQueryId,
                new[] { emptyResult },
                cacheTime: 0,
                isPersonal: true,
                button: BuildDeveloperInlineButton(),
                cancellationToken: cancellationToken);
        }

        private static InlineKeyboardMarkup BuildSourceMarkup(BingImageResult item)
        {
            string sourceUrl = item.SourceUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? item.SourceUrl
                : item.Url;
            string buttonText = item.SourceUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? "🌐 Перейти на сайт"
                : "🖼 Открыть оригинал";

            return new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl(buttonText, sourceUrl));
        }

        static async Task StartProxyAsync(int port, CancellationToken ct)
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://*:{port}/");
            try
            {
                listener.Start();
                Console.WriteLine($"Proxy started on port {port}");

                while (!ct.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string? b64Url = context.Request.QueryString["u"];
                            if (string.IsNullOrEmpty(b64Url))
                            {
                                context.Response.StatusCode = 400;
                                context.Response.Close();
                                return;
                            }

                            string url = Encoding.UTF8.GetString(Convert.FromBase64String(b64Url));
                            var bytes = await _httpClient.GetByteArrayAsync(url);
                            var processed = _watermarkService.ApplyWatermark(bytes);

                            context.Response.ContentType = "image/jpeg";
                            context.Response.ContentLength64 = processed.Length;
                            await context.Response.OutputStream.WriteAsync(processed, 0, processed.Length);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Proxy error: {ex.Message}");
                            context.Response.StatusCode = 500;
                        }
                        finally
                        {
                            context.Response.Close();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Listener error: {ex.Message}");
            }
        }
    }
}
