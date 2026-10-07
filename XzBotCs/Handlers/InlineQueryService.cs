using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using XzBotCs.Helpers;
using XzBotCs.Interfaces;
using XzBotCs.Models;
using XzBotCs.Services;

namespace XzBotCs.Handlers
{
    /// <summary>
    /// Обработка inline-запросов: поиск картинок, ленивое наложение ватермарки,
    /// проксирование URL, /logs-дашборд.
    /// </summary>
    public class InlineQueryService
    {
        private readonly ITelegramBotClient _botClient;
        private readonly BotState _state;
        private readonly HashSet<long> _adminIds;
        private readonly BotStatsService _statsService;
        private readonly ISearchService _searchService;
        private readonly SearchProviderRegistry _searchRegistry;
        private readonly WatermarkService _watermarkService;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _watermarkUploadLock;
        private readonly TimeSpan _flagRegexTimeout;

        public long? CacheChatId { get; set; }
        public string? ProxyBaseUrl { get; set; }
        public bool ProxyListenerStarted { get; set; }
        public string? BotUsername { get; set; }

        public InlineQueryService(
            ITelegramBotClient botClient,
            BotState state,
            HashSet<long> adminIds,
            BotStatsService statsService,
            ISearchService searchService,
            SearchProviderRegistry searchRegistry,
            WatermarkService watermarkService,
            HttpClient httpClient,
            SemaphoreSlim watermarkUploadLock,
            TimeSpan flagRegexTimeout,
            string? botUsername,
            long? cacheChatId,
            string? proxyBaseUrl)
        {
            _botClient = botClient;
            _state = state;
            _adminIds = adminIds;
            _statsService = statsService;
            _searchService = searchService;
            _searchRegistry = searchRegistry;
            _watermarkService = watermarkService;
            _httpClient = httpClient;
            _watermarkUploadLock = watermarkUploadLock;
            _flagRegexTimeout = flagRegexTimeout;
            BotUsername = botUsername;
            CacheChatId = cacheChatId;
            ProxyBaseUrl = proxyBaseUrl;
        }

        public async Task HandleAsync(InlineQuery inlineQuery, CancellationToken cancellationToken)
        {
            string query = inlineQuery.Query.Trim();
            if (string.IsNullOrEmpty(query))
            {
                await TelegramUiHelper.AnswerEmptyInlineQueryAsync(_botClient, inlineQuery.Id, _statsService, cancellationToken);
                return;
            }

            if (!_state.Subscribers.Contains(inlineQuery.From.Id) && !_adminIds.Contains(inlineQuery.From.Id))
            {
                await TelegramUiHelper.AnswerRegistrationRequiredAsync(_botClient, inlineQuery.Id, BotUsername, _statsService, cancellationToken);
                return;
            }

            if (query.StartsWith("/logs", StringComparison.OrdinalIgnoreCase))
            {
                await HandleLogsInlineQueryAsync(inlineQuery, cancellationToken);
                return;
            }

            bool isRandom = UserInputHelper.TryParseFlag(ref query, "--random", _flagRegexTimeout);
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
                            ReplyMarkup = TelegramUiHelper.BuildSourceMarkup(item)
                        });
                        continue;
                    }

                    finalUrl = BuildProxyImageUrl(item.Url);
                }

                if (item.IsGif)
                {
                    results.Add(new InlineQueryResultGif(item.Id, finalUrl, thumbnailUrl)
                    {
                        ReplyMarkup = TelegramUiHelper.BuildSourceMarkup(item)
                    });
                }
                else
                {
                    results.Add(new InlineQueryResultPhoto(item.Id, finalUrl, thumbnailUrl)
                    {
                        ReplyMarkup = TelegramUiHelper.BuildSourceMarkup(item)
                    });
                }
            }

            string nextOffset = searchResults.Count > 0 ? (offset + searchResults.Count).ToString() : "";
            bool answered = await TelegramUiHelper.TryAnswerInlineQueryAsync(
                _botClient,
                inlineQuery.Id,
                results,
                cacheTime: 300,
                isPersonal: false,
                statsService: _statsService,
                nextOffset: nextOffset,
                cancellationToken: cancellationToken);

            _statsService.RecordRequest(inlineQuery.From.Id, inlineQuery.From.Username, query, answered && searchResults.Count > 0 && string.IsNullOrEmpty(searchResponse.ErrorType));
        }

        private async Task HandleLogsInlineQueryAsync(InlineQuery inlineQuery, CancellationToken cancellationToken)
        {
            if (!_adminIds.Contains(inlineQuery.From.Id))
            {
                var noAccessResult = new InlineQueryResultArticle(
                    "no-access",
                    "⛔ Нет доступа",
                    new InputTextMessageContent("У вас нет доступа к логам."));

                await TelegramUiHelper.TryAnswerInlineQueryAsync(
                    _botClient,
                    inlineQuery.Id,
                    new[] { noAccessResult },
                    cacheTime: 0,
                    isPersonal: true,
                    statsService: _statsService,
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

            bool logsAnswered = await TelegramUiHelper.TryAnswerInlineQueryAsync(
                _botClient,
                inlineQuery.Id,
                new[] { logsResult },
                cacheTime: 10,
                isPersonal: true,
                statsService: _statsService,
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
                await TelegramUiHelper.TryAnswerInlineQueryAsync(
                    _botClient,
                    inlineQuery.Id,
                    new[] { fallbackResult },
                    cacheTime: 10,
                    isPersonal: true,
                    statsService: _statsService,
                    cancellationToken: cancellationToken);
            }
        }

        public string BuildProxyImageUrl(string imageUrl)
        {
            if (string.IsNullOrEmpty(ProxyBaseUrl) || !ProxyListenerStarted)
            {
                return imageUrl;
            }

            string b64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(imageUrl));
            return $"{ProxyBaseUrl}{WebUtility.UrlEncode(b64Url)}";
        }

        private async Task<string?> GetOrUploadWatermarkedPhotoFileIdAsync(BingImageResult item, CancellationToken cancellationToken)
        {
            if (_botClient == null || CacheChatId == null)
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
                    new ChatId(CacheChatId.Value),
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
    }
}
