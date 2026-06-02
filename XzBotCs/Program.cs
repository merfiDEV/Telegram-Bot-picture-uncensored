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
        private static readonly HashSet<long> _adminIds = new HashSet<long>();
        private static long? _cacheChatId;
        private static string? _proxyBaseUrl;
        private static volatile bool _proxyListenerStarted;
        private static readonly SemaphoreSlim _watermarkUploadLock = new SemaphoreSlim(3);

        private const int DefaultProxyPort = 8080;
        private const string DefaultProxyBaseUrl = "http://46.229.63.243:8080/img?u=";
        private const string DeveloperProfileUrl = "https://t.me/Tyta_Zdesyaa777";

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
            if (long.TryParse(cacheChatIdStr, out long cid)) _cacheChatId = cid;
            else _cacheChatId = _adminIds.Count > 0 ? _adminIds.First() : null;
            int proxyPort = int.TryParse(proxyPortStr, out int parsedProxyPort) ? parsedProxyPort : DefaultProxyPort;
            _proxyBaseUrl = NormalizeProxyBaseUrl(proxyBaseUrl ?? DefaultProxyBaseUrl);
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
            Console.WriteLine("Bot is running. Press Ctrl+C to exit.");
            
            // Keep app running until cancelled
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }

            _state.Save();
        }

        private static string Escape(string text)
        {
            return text.Replace("\\", "\\\\").Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[").Replace("]", "\\]").Replace("(", "\\(").Replace(")", "\\)").Replace("~", "\\~").Replace("`", "\\`").Replace(">", "\\>").Replace("#", "\\#").Replace("+", "\\+").Replace("-", "\\-").Replace("=", "\\=").Replace("|", "\\|").Replace("{", "\\{").Replace("}", "\\}").Replace(".", "\\.").Replace("!", "\\!");
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
                        if (_adminIds.Count == 0 || message.From?.Id == null || !_adminIds.Contains(message.From.Id))
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

                        if (_adminIds.Count == 0 || message.From?.Id == null || !_adminIds.Contains(message.From.Id))
                        {
                            await botClient.SendMessage(message.Chat.Id, "Нет доступа :/", cancellationToken: cancellationToken);
                            return;
                        }

                        string logsPath = "../logs";
                        if (!Directory.Exists(logsPath)) Directory.CreateDirectory(logsPath);

                        string zipPath = Path.Combine(Path.GetTempPath(), $"logs_{Guid.NewGuid():N}.zip");
                        try
                        {
                            using (var fs = new FileStream(zipPath, FileMode.Create))
                            using (var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                            {
                                foreach (var file in Directory.GetFiles(logsPath))
                                {
                                    var entry = archive.CreateEntry(Path.GetFileName(file));
                                    using var entryStream = entry.Open();
                                    using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                    fileStream.CopyTo(entryStream);
                                }
                            }

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
                    else if (messageText.StartsWith("/setwatermark"))
                    {
                        if (_adminIds.Count == 0 || message.From?.Id == null || !_adminIds.Contains(message.From.Id))
                        {
                            await botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                            return;
                        }

                        string cmdPrefix = "/setwatermark";
                        if (messageText.StartsWith("/setwatermark@"))
                        {
                            int spaceIndex = messageText.IndexOf(' ');
                            cmdPrefix = spaceIndex >= 0 ? messageText.Substring(0, spaceIndex) : messageText;
                        }

                        string newWatermark = messageText.Substring(cmdPrefix.Length).Trim();
                        if (string.IsNullOrEmpty(newWatermark))
                        {
                            await botClient.SendMessage(
                                message.Chat.Id,
                                "⚠️ Укажите новый текст водяного знака\\.\nПример: `/setwatermark Новый Текст`",
                                parseMode: ParseMode.MarkdownV2,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        _state.WatermarkText = newWatermark;
                        _state.WatermarkFileIds.Clear();
                        _state.Save();

                        await botClient.SendMessage(
                            message.Chat.Id,
                            $"✅ Текст водяного знака изменен на: `{Escape(newWatermark)}`\\.\nКэш старых ватермарок в Telegram очищен\\.",
                            parseMode: ParseMode.MarkdownV2,
                            cancellationToken: cancellationToken);
                    }
                }
                else if (update.CallbackQuery is { } callbackQuery)
                {
                    if (_adminIds.Count == 0 || !_adminIds.Contains(callbackQuery.From.Id))
                    {
                        await TryAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "⛔ Нет доступа", showAlert: true, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "toggle_wm")
                    {
                        _state.IsWatermarkEnabled = !_state.IsWatermarkEnabled;
                        _state.Save();
                        await TryAnswerCallbackQueryAsync(botClient, callbackQuery.Id, $"Ватермарка: {(_state.IsWatermarkEnabled ? "ВКЛ" : "ВЫКЛ")}", cancellationToken: cancellationToken);
                        await RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
                    }
                    else if (callbackQuery.Data == "stats:refresh")
                    {
                        await RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
                        await TryAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "Обновлено ✅", cancellationToken: cancellationToken);
                    }
                    else if (callbackQuery.Data == "stats:back")
                    {
                        await RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
                        await TryAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
                    }
                    else if (callbackQuery.Data == "stats:metrics")
                    {
                        string text = _statsService.BuildMetricsText();
                        var markup = new InlineKeyboardMarkup(new[] { 
                            new [] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "stats:back") },
                            new [] { InlineKeyboardButton.WithCallbackData("🔄 Обновить", "stats:metrics") }
                        });
                        try
                        {
                            if (callbackQuery.Message is Message { Type: MessageType.Photo } photoMsg)
                            {
                                await botClient.EditMessageCaption(photoMsg.Chat.Id, photoMsg.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: cancellationToken);
                            }
                            else
                            {
                                await botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: cancellationToken);
                            }
                        }
                        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified")) { }
                        
                        await TryAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
                    }
                    else if (callbackQuery.Data == "stats:dashboard")
                    {
                        string text = _statsService.BuildDashboardText();
                        var markup = new InlineKeyboardMarkup(new[] {
                            new [] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "stats:back") },
                            new [] { InlineKeyboardButton.WithCallbackData("🔄 Обновить", "stats:dashboard") }
                        });
                        try
                        {
                            if (callbackQuery.Message is Message { Type: MessageType.Photo } photoMsg)
                            {
                                await botClient.EditMessageCaption(photoMsg.Chat.Id, photoMsg.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: cancellationToken);
                            }
                            else
                            {
                                await botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: cancellationToken);
                            }
                        }
                        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified")) { }

                        await TryAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
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
                    int resultLimit = _state.IsWatermarkEnabled ? 6 : 30;
                    var searchResponse = await _searchService.SearchImagesDetailedAsync(query, startIndex: offset + 1, limit: resultLimit);
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

                    var watermarkedFileIds = new Dictionary<string, string?>();
                    if (_state.IsWatermarkEnabled)
                    {
                        var uploadTasks = searchResults
                            .Where(item => !item.IsGif)
                            .Select(async item => (item.Id, FileId: await GetOrUploadWatermarkedPhotoFileIdAsync(item, cancellationToken)))
                            .ToArray();

                        foreach (var upload in await Task.WhenAll(uploadTasks))
                        {
                            watermarkedFileIds[upload.Id] = upload.FileId;
                        }
                    }

                    var results = new List<InlineQueryResult>();
                    foreach (var item in searchResults)
                    {
                        string finalUrl = item.Url;
                        string thumbnailUrl = string.IsNullOrEmpty(item.ThumbnailUrl) ? item.Url : item.ThumbnailUrl;
                        if (_state.IsWatermarkEnabled && !item.IsGif)
                        {
                            watermarkedFileIds.TryGetValue(item.Id, out string? fileId);
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

                    string nextOffset = searchResponse.ConsumedCount > 0 ? (offset + searchResponse.ConsumedCount).ToString() : "";
                    bool answered = await TryAnswerInlineQueryAsync(
                        botClient,
                        inlineQuery.Id,
                        results,
                        cacheTime: 300,
                        isPersonal: false,
                        nextOffset: nextOffset,
                        cancellationToken: cancellationToken);

                    _statsService.RecordRequest(inlineQuery.From.Id, inlineQuery.From.Username, query, answered && searchResults.Count > 0 && string.IsNullOrEmpty(searchResponse.ErrorType));
                    _state.Save();
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

        static async Task SendStatsAsync(long chatId, CancellationToken ct)
        {
            var (bingOk, bingStatus) = await _searchService.CheckBingAsync();
            var text = _statsService.BuildStatsText(bingOk, bingStatus);
            var markup = BuildStatsMarkup();
            
            var chartBytes = _statsService.GenerateChartImage();
            if (chartBytes.Length > 0)
            {
                using var ms = new MemoryStream(chartBytes);
                await _botClient!.SendPhoto(chatId, InputFile.FromStream(ms, "stats.png"), caption: text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
            }
            else
            {
                await _botClient!.SendMessage(chatId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
            }
        }

        static async Task RefreshStatsAsync(long chatId, int messageId, CancellationToken ct)
        {
            var (bingOk, bingStatus) = await _searchService.CheckBingAsync();
            var text = _statsService.BuildStatsText(bingOk, bingStatus);
            var markup = BuildStatsMarkup();
            try
            {
                var chartBytes = _statsService.GenerateChartImage();
                if (chartBytes.Length > 0)
                {
                    using var ms = new MemoryStream(chartBytes);
                    await _botClient!.EditMessageMedia(chatId, messageId, new InputMediaPhoto(InputFile.FromStream(ms, "stats.png")), cancellationToken: ct);
                    await _botClient!.EditMessageCaption(chatId, messageId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                }
                else
                {
                    try
                    {
                        await _botClient!.EditMessageText(chatId, messageId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                    }
                    catch (ApiRequestException ex) when (ex.Message.Contains("there is no text in the message to edit"))
                    {
                        await _botClient!.EditMessageCaption(chatId, messageId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                    }
                }
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified")) { }
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

            if (_state.WatermarkFileIds.TryGetValue(item.Id, out string? cachedFileId) && !string.IsNullOrEmpty(cachedFileId))
            {
                return cachedFileId;
            }

            await _watermarkUploadLock.WaitAsync(cancellationToken);
            try
            {
                if (_state.WatermarkFileIds.TryGetValue(item.Id, out cachedFileId) && !string.IsNullOrEmpty(cachedFileId))
                {
                    return cachedFileId;
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

                _state.WatermarkFileIds[item.Id] = fileId;
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

            await TryAnswerInlineQueryAsync(
                botClient,
                inlineQueryId,
                new[] { emptyResult },
                cacheTime: 0,
                isPersonal: true,
                cancellationToken: cancellationToken);
        }

        private static async Task<bool> TryAnswerInlineQueryAsync(
            ITelegramBotClient botClient,
            string inlineQueryId,
            IEnumerable<InlineQueryResult> results,
            int cacheTime,
            bool isPersonal,
            string nextOffset = "",
            CancellationToken cancellationToken = default)
        {
            try
            {
                await botClient.AnswerInlineQuery(
                    inlineQueryId,
                    results,
                    cacheTime: cacheTime,
                    isPersonal: isPersonal,
                    nextOffset: nextOffset,
                    button: BuildDeveloperInlineButton(),
                    cancellationToken: cancellationToken);
                return true;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && (
                ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("query expired", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase)))
            {
                _statsService.RecordError("inline_timeout");
                Console.WriteLine($"Inline answer skipped: Telegram query expired ({ex.Message})");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error answering inline query: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> TryAnswerCallbackQueryAsync(
            ITelegramBotClient botClient,
            string callbackQueryId,
            string? text = null,
            bool showAlert = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await botClient.AnswerCallbackQuery(callbackQueryId, text, showAlert, cancellationToken: cancellationToken);
                return true;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && (
                ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("query expired", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"Callback answer skipped: Telegram query expired ({ex.Message})");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error answering callback query: {ex.Message}");
                return false;
            }
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
