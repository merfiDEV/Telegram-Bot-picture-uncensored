using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using XzBotCs.Interfaces;
using XzBotCs.Models;
using XzBotCs.Services;

namespace XzBotCs.Handlers
{
    public class InlineQueryHandler
    {
        private readonly ITelegramBotClient _botClient;
        private readonly ISearchService _searchService;
        private readonly IWatermarkService _watermarkService;
        private readonly IStatsService _statsService;
        private readonly BotState _state;
        private readonly AppConfig _config;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _watermarkUploadLock = new SemaphoreSlim(3);

        public InlineQueryHandler(
            ITelegramBotClient botClient,
            ISearchService searchService,
            IWatermarkService watermarkService,
            IStatsService statsService,
            BotState state,
            AppConfig config,
            HttpClient httpClient)
        {
            _botClient = botClient;
            _searchService = searchService;
            _watermarkService = watermarkService;
            _statsService = statsService;
            _state = state;
            _config = config;
            _httpClient = httpClient;
        }

        private async Task HandleLogsInlineQueryAsync(string inlineQueryId, long userId, CancellationToken ct)
        {
            if (!_config.AdminIds.Contains(userId))
            {
                var noAccessResult = new InlineQueryResultArticle(
                    "no-access",
                    "⛔ Нет доступа",
                    new InputTextMessageContent("У вас нет доступа к логам."));
                
                await TryAnswerInlineQueryAsync(
                    inlineQueryId,
                    new[] { noAccessResult },
                    cacheTime: 0,
                    isPersonal: true,
                    cancellationToken: ct);
                return;
            }

var dashboardText = _statsService.BuildDashboardText();
            
            var result = new InlineQueryResultArticle(
                "logs-dashboard",
                "📋 Логи запросов",
                new InputTextMessageContent(dashboardText) { ParseMode = Telegram.Bot.Types.Enums.ParseMode.MarkdownV2 })
            {
                Description = "Последние 10 запросов"
            };

            await TryAnswerInlineQueryAsync(
                inlineQueryId,
                new[] { result },
                cacheTime: 10,
                isPersonal: true,
                cancellationToken: ct);
        }

        public async Task HandleInlineQueryAsync(InlineQuery inlineQuery, string proxyBaseUrl, bool proxyListenerStarted, CancellationToken ct)
        {
            string query = inlineQuery.Query.Trim();
            if (string.IsNullOrEmpty(query))
            {
                await AnswerEmptyInlineQuery(inlineQuery.Id, ct);
                return;
            }

            if (query.Trim().StartsWith("/logs", StringComparison.OrdinalIgnoreCase))
            {
                await HandleLogsInlineQueryAsync(inlineQuery.Id, inlineQuery.From.Id, ct);
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
                    .Select(async item => (item.Id, FileId: await GetOrUploadWatermarkedPhotoFileIdAsync(item, ct)))
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

                    finalUrl = BuildProxyImageUrl(item.Url, proxyBaseUrl, proxyListenerStarted);
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
                inlineQuery.Id,
                results,
                cacheTime: 300,
                isPersonal: false,
                nextOffset: nextOffset,
                cancellationToken: ct);

            _statsService.RecordRequest(inlineQuery.From.Id, inlineQuery.From.Username, query, answered && searchResults.Count > 0 && string.IsNullOrEmpty(searchResponse.ErrorType));
            _state.Save();
        }

        private async Task AnswerEmptyInlineQuery(string inlineQueryId, CancellationToken ct)
        {
            var emptyResult = new InlineQueryResultArticle(
                "empty-query",
                "🔍 Введите запрос",
                new InputTextMessageContent("Введите запрос после имени бота, и я найду картинки."))
            {
                Description = "Напишите, какую картинку найти. Например: кот в очках"
            };

            await TryAnswerInlineQueryAsync(
                inlineQueryId,
                new[] { emptyResult },
                cacheTime: 0,
                isPersonal: true,
                cancellationToken: ct);
        }

        private async Task<bool> TryAnswerInlineQueryAsync(
            string inlineQueryId,
            IEnumerable<InlineQueryResult> results,
            int cacheTime,
            bool isPersonal,
            string nextOffset = "",
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _botClient.AnswerInlineQuery(
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

        private async Task<string?> GetOrUploadWatermarkedPhotoFileIdAsync(BingImageResult item, CancellationToken ct)
        {
            if (_config.CacheChatId == null)
            {
                return null;
            }

            if (_state.WatermarkFileIds.TryGetValue(item.Id, out string? cachedFileId) && !string.IsNullOrEmpty(cachedFileId))
            {
                return cachedFileId;
            }

            await _watermarkUploadLock.WaitAsync(ct);
            try
            {
                if (_state.WatermarkFileIds.TryGetValue(item.Id, out cachedFileId) && !string.IsNullOrEmpty(cachedFileId))
                {
                    return cachedFileId;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                var result = _watermarkService.ApplyWatermarkOrOriginal(bytes, contentType, _state.WatermarkText);
                if (!result.IsWatermarked)
                {
                    Console.WriteLine($"Watermark cache skipped unsupported image: {item.Url}");
                    return null;
                }

                using var stream = new MemoryStream(result.Bytes);
                var message = await _botClient.SendPhoto(
                    new ChatId(_config.CacheChatId.Value),
                    InputFile.FromStream(stream, $"{item.Id}.jpg"),
                    disableNotification: true,
                    cancellationToken: ct);

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

        private static string BuildProxyImageUrl(string imageUrl, string? proxyBaseUrl, bool proxyListenerStarted)
        {
            if (string.IsNullOrEmpty(proxyBaseUrl) || !proxyListenerStarted)
            {
                return imageUrl;
            }

            string b64Url = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(imageUrl));
            return $"{proxyBaseUrl}{System.Net.WebUtility.UrlEncode(b64Url)}";
        }
    }
}
