using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using XzBotCs.Helpers;
using XzBotCs.Interfaces;
using XzBotCs.Models;
using XzBotCs.Services;

namespace XzBotCs
{
    class Program
    {
        private static ITelegramBotClient? _botClient;
        private static BotState _state = BotState.Load();
                private static SearchProviderRegistry _searchRegistry = new SearchProviderRegistry(_state);
                private static ISearchService _searchService = new RoutingSearchService(_searchRegistry);
                private static WatermarkService _watermarkService = new WatermarkService();
        private static BotStatsService _statsService = new BotStatsService(_state);
        private static Commands.BroadcastService _broadcastService = null!;
        private static Services.StatsMessageService _statsMessageService = null!;
        private static Handlers.CallbackQueryHandler _callbackQueryHandler = null!;
        private static Handlers.MessageCommandHandler _messageCommandHandler = null!;
        private static PrefStore _prefs = PrefStore.Load();
        private static HttpClient _httpClient = new HttpClient();
        private static readonly HashSet<long> _adminIds = new HashSet<long>();
        private static long? _cacheChatId;
        private static string? _proxyBaseUrl;
        private static string? _botUsername;
        private static volatile bool _proxyListenerStarted;
        private static readonly SemaphoreSlim _watermarkUploadLock = new SemaphoreSlim(3);
        private static readonly TimeSpan FlagRegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly object _stateLock = new object();

        private const int DefaultProxyPort = 8080;
        private const string DefaultProxyBaseUrl = "http://46.229.63.243:8080/img?u=";

        static async Task Main(string[] args)
        {
            string? token = Environment.GetEnvironmentVariable("BOT_TOKEN");
            string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_ID");
            string? cacheChatIdStr = Environment.GetEnvironmentVariable("CACHE_CHAT_ID");
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
                    if (line.StartsWith("CACHE_CHAT_ID=")) cacheChatIdStr = ReadEnvValue(line, "CACHE_CHAT_ID=");
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

            if (!string.IsNullOrEmpty(adminIdStr))
            {
                foreach (var part in adminIdStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (long.TryParse(part, out long aid)) _adminIds.Add(aid);
                }
            }
            foreach (var extraAdmin in _state.ExtraAdmins)
            {
                _adminIds.Add(extraAdmin);
            }
            if (long.TryParse(cacheChatIdStr, out long cid)) _cacheChatId = cid;
            else _cacheChatId = _adminIds.Count > 0 ? _adminIds.First() : null;
            int proxyPort = int.TryParse(proxyPortStr, out int parsedProxyPort) ? parsedProxyPort : DefaultProxyPort;
            _proxyBaseUrl = UserInputHelper.NormalizeProxyBaseUrl(proxyBaseUrl ?? DefaultProxyBaseUrl);
            SetupLogging();

            _botClient = new TelegramBotClient(token);
            _broadcastService = new Commands.BroadcastService(_botClient, _state, _searchService, _statsService, _httpClient);
            _statsMessageService = new Services.StatsMessageService(_botClient, _searchRegistry, _statsService, _state);
            _callbackQueryHandler = new Handlers.CallbackQueryHandler(_botClient, _state, _searchRegistry, _statsService, _statsMessageService, _adminIds);
            _messageCommandHandler = new Handlers.MessageCommandHandler(_botClient, _state, _prefs, _adminIds, _statsService, _broadcastService, _statsMessageService, _searchRegistry);

            using var cts = new CancellationTokenSource();

                        // Graceful shutdown: по Ctrl+C отменяем задачи, чтобы Main успел сохранить состояние.
                        Console.CancelKeyPress += (_, e) =>
                        {
                            e.Cancel = true;
                            Console.WriteLine("Shutdown requested, stopping...");
                            cts.Cancel();
                        };
                        AppDomain.CurrentDomain.ProcessExit += (_, _) => _state.Save();

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
            _botUsername = me.Username;
            Console.WriteLine($"Start listening for @{me.Username}");
            Console.WriteLine(string.IsNullOrEmpty(_proxyBaseUrl)
                ? "Watermark proxy URL is not configured. Set PROXY_BASE_URL, for example: https://example.com/img?u="
                : $"Watermark proxy URL: {_proxyBaseUrl}");
            Console.WriteLine("Bot is running. Press Ctrl+C to exit.");
            
            // Keep app running until cancelled
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }

            _state.Save();
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
            var fileStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var fileWriter = TextWriter.Synchronized(new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true });
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
            try
            {
                if (update.Message is { } message && message.Text is { } messageText)
                {
                    await _messageCommandHandler.HandleAsync(message, messageText, cancellationToken);
                }
                else if (update.CallbackQuery is { } callbackQuery)
                {
                    await _callbackQueryHandler.HandleAsync(callbackQuery, cancellationToken);
                }
                else if (update.InlineQuery is { } inlineQuery)
                {
                    string query = inlineQuery.Query.Trim();
                    if (string.IsNullOrEmpty(query))
                    {
                        await AnswerEmptyInlineQuery(botClient, inlineQuery.Id, cancellationToken);
                        return;
                    }

                    if (!_state.Subscribers.Contains(inlineQuery.From.Id) && !_adminIds.Contains(inlineQuery.From.Id))
                    {
                        await AnswerRegistrationRequired(botClient, inlineQuery.Id, cancellationToken);
                        return;
                    }

                    if (query.StartsWith("/logs", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_adminIds.Contains(inlineQuery.From.Id))
                        {
                            var noAccessResult = new InlineQueryResultArticle(
                                "no-access",
                                "⛔ Нет доступа",
                                new InputTextMessageContent("У вас нет доступа к логам."));

                            await TryAnswerInlineQueryAsync(
                                botClient,
                                inlineQuery.Id,
                                new[] { noAccessResult },
                                cacheTime: 0,
                                isPersonal: true,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        var dashboardText = _statsService.BuildDashboardText(inlineQuery.From.Id);

                        var logsResult = new InlineQueryResultArticle(
                            "logs-dashboard",
                            "📋 Логи запросов",
                            new InputTextMessageContent(dashboardText) { ParseMode = ParseMode.MarkdownV2 })
                        {
                            Description = "Последние 10 запросов"
                        };

                        bool logsAnswered = await TryAnswerInlineQueryAsync(
                            botClient,
                            inlineQuery.Id,
                            new[] { logsResult },
                            cacheTime: 10,
                            isPersonal: true,
                            cancellationToken: cancellationToken);
                        if (!logsAnswered)
                        {
                            // Fallback без MarkdownV2 если парсинг упал
                            var fallbackResult = new InlineQueryResultArticle(
                                "logs-dashboard",
                                "📋 Логи запросов",
                                new InputTextMessageContent(dashboardText))
                            {
                                Description = "Последние 10 запросов"
                            };
                            await TryAnswerInlineQueryAsync(
                                botClient,
                                inlineQuery.Id,
                                new[] { fallbackResult },
                                cacheTime: 10,
                                isPersonal: true,
                                cancellationToken: cancellationToken);
                        }
                        return;
                    }

                    bool isRandom = UserInputHelper.TryParseFlag(ref query, "--random", FlagRegexTimeout);
                                        int offset = int.TryParse(inlineQuery.Offset, out int parsedOffset) ? parsedOffset : 0;
                                        Console.WriteLine($"Inline query from {inlineQuery.From.Id}: '{query}', offset={offset}, random={isRandom}");
                                        _statsService.IncrementUsage();
                                        int resultLimit = _state.IsWatermarkEnabled ? 6 : 30;
                                        var searchResponse = await _searchService.SearchImagesDetailedAsync(query, startIndex: offset + 1, limit: resultLimit);
                                        if (searchResponse.ResponseTime > TimeSpan.Zero)
                                        {
                                            _statsService.RecordResponseTime(searchResponse.ResponseTime);
                                            _statsService.RecordResponseTime(_searchRegistry.ActiveKey, searchResponse.ResponseTime);
                                        }
                                        if (!string.IsNullOrEmpty(searchResponse.ErrorType))
                                        {
                                            _statsService.RecordError(searchResponse.ErrorType);
                                        }

                                        var searchResults = searchResponse.Items;
                                                                                Console.WriteLine($"Search returned {searchResults.Count} results for '{query}'");

                    // При --random заранее оставляем только один элемент, чтобы не грузить ватермарку зря.
                    var selectedItems = isRandom && searchResults.Count > 1
                        ? new List<BingImageResult> { searchResults[Random.Shared.Next(searchResults.Count)] }
                        : searchResults;

                    var results = new List<InlineQueryResult>();
                    foreach (var item in selectedItems)
                    {
                        string finalUrl = item.Url;
                        string thumbnailUrl = string.IsNullOrEmpty(item.ThumbnailUrl) ? item.Url : item.ThumbnailUrl;
                        if (_state.IsWatermarkEnabled && !item.IsGif)
                        {
                            // Ленивая загрузка: ватермарка накладывается только на элементы,
                            // реально попадающие в ответ (а не на все результаты поиска).
                            string? fileId = await GetOrUploadWatermarkedPhotoFileIdAsync(item, cancellationToken);
                            if (!string.IsNullOrEmpty(fileId))
                            {
                                results.Add(new InlineQueryResultCachedPhoto(item.Id, fileId)
                                {
                                    ReplyMarkup = BuildSourceMarkup(item)
                                });
                                continue;
                            }

                            finalUrl = item.Url;
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

                    string nextOffset = searchResults.Count > 0 ? (offset + searchResults.Count).ToString() : "";
                    bool answered = await TryAnswerInlineQueryAsync(
                        botClient,
                        inlineQuery.Id,
                        results,
                        cacheTime: 300,
                        isPersonal: false,
                        nextOffset: nextOffset,
                        cancellationToken: cancellationToken);

                    _statsService.RecordRequest(inlineQuery.From.Id, inlineQuery.From.Username, query, answered && searchResults.Count > 0 && string.IsNullOrEmpty(searchResponse.ErrorType));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update handler error: {ex}");
            }
        }

        static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine(exception);
            return Task.CompletedTask;
        }

        static Task SendRandomImageAsync(long chatId, string topic, CancellationToken ct)
            => _broadcastService.SendRandomImageAsync(chatId, topic, ct);

        static Task SendBroadcastAsync(long adminChatId, string announcement, CancellationToken ct)
            => _broadcastService.SendBroadcastAsync(adminChatId, announcement, ct);

        static Task SendStatsAsync(long chatId, CancellationToken ct)
            => _statsMessageService.SendStatsAsync(chatId, ct);

        static Task RefreshStatsAsync(long chatId, int messageId, CancellationToken ct)
            => _statsMessageService.RefreshStatsAsync(chatId, messageId, ct);

        static InlineKeyboardMarkup BuildStatsMarkup()
            => TelegramUiHelper.BuildStatsMarkup(_state.IsWatermarkEnabled, _searchRegistry.ActiveKey);

        private static Task EditDashboardWithFallbackAsync(ITelegramBotClient botClient, Message message, string text, InlineKeyboardMarkup markup, CancellationToken ct)
            => TelegramUiHelper.EditDashboardWithFallbackAsync(botClient, message, text, markup, ct);

        private static string BuildProxyImageUrl(string imageUrl)
        {
            if (string.IsNullOrEmpty(_proxyBaseUrl) || !_proxyListenerStarted)
            {
                return imageUrl;
            }

            string b64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(imageUrl));
            return $"{_proxyBaseUrl}{WebUtility.UrlEncode(b64Url)}";
        }

        private static async Task<string?> GetOrUploadWatermarkedPhotoFileIdAsync(BingImageResult item, CancellationToken cancellationToken)
        {
            if (_botClient == null || _cacheChatId == null)
            {
                return null;
            }

            lock (_state.SyncRoot)
            {
                if (_state.WatermarkFileIds.TryGetValue(item.Id, out string? cachedFileId) && !string.IsNullOrEmpty(cachedFileId))
                {
                    return cachedFileId;
                }
            }

            await _watermarkUploadLock.WaitAsync(cancellationToken);
            try
            {
                lock (_state.SyncRoot)
                {
                    if (_state.WatermarkFileIds.TryGetValue(item.Id, out string? cachedFileId) && !string.IsNullOrEmpty(cachedFileId))
                    {
                        return cachedFileId;
                    }
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Watermark cache download failed: {(int)response.StatusCode} {item.Url}");
                    return null;
                }

                string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Watermark cache skipped non-image content-type '{contentType}': {item.Url}");
                    return null;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var result = _watermarkService.ApplyWatermarkOrOriginal(bytes, contentType, _state.WatermarkText);
                if (!result.IsWatermarked)
                {
                    Console.WriteLine($"Watermark cache skipped unsupported image: {item.Url}");
                    return null;
                }

                using var stream = new MemoryStream(result.Bytes);
                var message = await _botClient.SendPhoto(
                    new ChatId(_cacheChatId.Value),
                    InputFile.FromStream(stream, $"{item.Id}.jpg"),
                    disableNotification: true,
                    cancellationToken: cancellationToken);

                string? fileId = message.Photo?.OrderByDescending(photo => photo.Width * photo.Height).FirstOrDefault()?.FileId;
                if (string.IsNullOrEmpty(fileId))
                {
                    Console.WriteLine($"Watermark cache upload did not return photo file_id: {item.Url}");
                    return null;
                }

                lock (_state.SyncRoot)
                {
                    _state.WatermarkFileIds[item.Id] = fileId;
                }
                _state.Save();
                return fileId;
            }
            catch (Exception ex)
            {
                _statsService.RecordError("watermark_cache");
                Console.WriteLine($"Watermark cache error: {ex.Message}");
                return null;
            }
            finally
            {
                _watermarkUploadLock.Release();
            }
        }

        private static InlineQueryResultsButton BuildDeveloperInlineButton()
            => TelegramUiHelper.BuildDeveloperInlineButton();

        private static Task AnswerRegistrationRequired(ITelegramBotClient botClient, string inlineQueryId, CancellationToken cancellationToken)
            => TelegramUiHelper.AnswerRegistrationRequiredAsync(botClient, inlineQueryId, _botUsername, _statsService, cancellationToken);

        private static Task AnswerEmptyInlineQuery(ITelegramBotClient botClient, string inlineQueryId, CancellationToken cancellationToken)
            => TelegramUiHelper.AnswerEmptyInlineQueryAsync(botClient, inlineQueryId, _statsService, cancellationToken);

        private static Task<bool> TryAnswerInlineQueryAsync(
            ITelegramBotClient botClient,
            string inlineQueryId,
            IEnumerable<InlineQueryResult> results,
            int cacheTime,
            bool isPersonal,
            string nextOffset = "",
            CancellationToken cancellationToken = default)
            => TelegramUiHelper.TryAnswerInlineQueryAsync(botClient, inlineQueryId, results, cacheTime, isPersonal, _statsService, nextOffset, cancellationToken);

        private static Task<bool> TryAnswerCallbackQueryAsync(
            ITelegramBotClient botClient,
            string callbackQueryId,
            string? text = null,
            bool showAlert = false,
            CancellationToken cancellationToken = default)
            => TelegramUiHelper.TryAnswerCallbackQueryAsync(botClient, callbackQueryId, text, showAlert, cancellationToken);

        private static InlineKeyboardMarkup BuildSourceMarkup(BingImageResult item)
            => TelegramUiHelper.BuildSourceMarkup(item);

        static async Task StartProxyAsync(int port, CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
                _proxyListenerStarted = true;
                Console.WriteLine($"Proxy started on port {port}");

                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleProxyClientAsync(client, ct), ct);
                }
            }
            catch (OperationCanceledException)
            {
                _proxyListenerStarted = false;
                Console.WriteLine("Proxy listener stopped.");
            }
            catch (Exception ex)
            {
                _proxyListenerStarted = false;
                Console.WriteLine($"Listener error: {ex.Message}");
                Console.WriteLine("Watermark proxy disabled. Inline results will use original image URLs.");
            }
            finally
            {
                listener.Stop();
            }
        }

        static async Task HandleProxyClientAsync(TcpClient client, CancellationToken ct)
        {
            await using var stream = client.GetStream();
            using (client)
            {
                try
                {
                    string requestText = await ReadHttpRequestAsync(stream, ct);
                    string? b64Url = ExtractProxyUrlParameter(requestText);
                    if (string.IsNullOrEmpty(b64Url))
                    {
                        await WriteTextResponseAsync(stream, 400, "Bad Request", "Missing u parameter", ct);
                        return;
                    }

                    string url = Encoding.UTF8.GetString(Convert.FromBase64String(b64Url));
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                    request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");

                    using var imageResponse = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!imageResponse.IsSuccessStatusCode)
                    {
                        await WriteTextResponseAsync(stream, (int)imageResponse.StatusCode, imageResponse.ReasonPhrase ?? "Upstream Error", "Upstream image request failed", ct);
                        return;
                    }

                    string originalContentType = imageResponse.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                    if (!originalContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteTextResponseAsync(stream, 415, "Unsupported Media Type", "Upstream response is not an image", ct);
                        return;
                    }

                    var bytes = await imageResponse.Content.ReadAsByteArrayAsync(ct);
                    var result = _watermarkService.ApplyWatermarkOrOriginal(bytes, originalContentType, _state.WatermarkText);
                    if (!result.IsWatermarked)
                    {
                        await WriteRedirectResponseAsync(stream, url, ct);
                        return;
                    }

                    await WriteBinaryResponseAsync(stream, result.ContentType, result.Bytes, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Proxy error: {ex.Message}");
                    await WriteTextResponseAsync(stream, 500, "Internal Server Error", "Proxy error", CancellationToken.None);
                }
            }
        }

        static async Task<string> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            int total = 0;

            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (read <= 0) break;

                total += read;
                string current = Encoding.ASCII.GetString(buffer, 0, total);
                if (current.Contains("\r\n\r\n")) return current;
            }

            return Encoding.ASCII.GetString(buffer, 0, total);
        }

        static string? ExtractProxyUrlParameter(string requestText)
        {
            string firstLine = requestText.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? "";
            string[] parts = firstLine.Split(' ');
            if (parts.Length < 2) return null;

            string target = parts[1];
            int queryIndex = target.IndexOf("?u=", StringComparison.OrdinalIgnoreCase);
            if (queryIndex < 0) return null;

            string value = target.Substring(queryIndex + 3);
            int ampIndex = value.IndexOf('&');
            if (ampIndex >= 0) value = value.Substring(0, ampIndex);

            return WebUtility.UrlDecode(value);
        }

        static async Task WriteBinaryResponseAsync(NetworkStream stream, string contentType, byte[] bytes, CancellationToken ct)
        {
            string headers =
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bytes.Length}\r\n" +
                "Cache-Control: public, max-age=86400\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
            await stream.WriteAsync(bytes, ct);
        }

        static async Task WriteRedirectResponseAsync(NetworkStream stream, string location, CancellationToken ct)
        {
            string headers =
                "HTTP/1.1 302 Found\r\n" +
                $"Location: {location}\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
        }

        static async Task WriteTextResponseAsync(NetworkStream stream, int statusCode, string reason, string body, CancellationToken ct)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers =
                $"HTTP/1.1 {statusCode} {reason}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
            await stream.WriteAsync(bodyBytes, ct);
        }
    }
}
