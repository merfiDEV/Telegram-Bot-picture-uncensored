using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XzBotCs.Services
{
    /// <summary>
    /// Поиск изображений через DuckDuckGo.
    ///
    /// DuckDuckGo отдаёт картинки через внутренний JSON-эндпоинт /i.js, который требует
    /// одноразовый токен vqd и корректную cookie-сессию. Реализовано несколько стратегий
    /// по убыванию надёжности; сервис перебирает их, пока не получит результаты:
    ///
    ///   1. vqd из HTML главной страницы + cookie-сессия + /i.js  (основной путь)
    ///   2. vqd из /js/vqd.js  (некоторые регионы отдают его отдельно)
    ///   3. Парсинг HTML-версии выдачи (html.duckduckgo.com) — без токена
    ///   4. Парсинг встроенного JSON из HTML главной страницы
    /// </summary>
    public class DuckDuckGoSearchService
    {
        private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(18);
        private static readonly TimeSpan VqdCacheTtl = TimeSpan.FromMinutes(10);
        private static readonly object SearchCacheLock = new object();
        private static readonly object VqdLock = new object();
        private static readonly Dictionary<string, CachedSearchResponse> SearchCache =
            new Dictionary<string, CachedSearchResponse>();
        private static readonly Dictionary<string, CachedVqd> VqdCache = new Dictionary<string, CachedVqd>();

        static DuckDuckGoSearchService()
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        }

        public async Task<BingSearchResponse> SearchImagesDetailedAsync(string query, int startIndex = 1, int limit = 30)
        {
            bool isGifSearch = TryParseGifFlag(ref query);
            if (string.IsNullOrWhiteSpace(query))
            {
                return new BingSearchResponse { ErrorType = "empty_query" };
            }

            // DuckDuckGo нумерует страницы, а не отдельные элементы.
            int page = Math.Max(1, (startIndex + limit - 1) / Math.Max(1, limit));

            string cacheKey = BuildSearchCacheKey(query, page, limit, isGifSearch);
            if (TryGetCachedSearchResponse(cacheKey, out var cached))
            {
                return cached;
            }

            var response = new BingSearchResponse();
            var stopwatch = Stopwatch.StartNew();
            var collected = new List<BingImageResult>();

            try
            {
                var strategies = new (string Name, Func<string, int, int, bool, Task<List<BingImageResult>>> Run)[]
                {
                    ("i.js/vqd", FetchViaVqdJsonAsync),
                    ("vqd.js", FetchViaVqdJsAsync),
                    ("html", FetchViaHtmlAsync),
                    ("embedded", FetchViaEmbeddedJsonAsync)
                };

                foreach (var strategy in strategies)
                {
                    try
                    {
                        var items = await strategy.Run(query, page, limit, isGifSearch);
                        if (items.Count > 0)
                        {
                            collected = items;
                            Console.WriteLine($"DuckDuckGo[{strategy.Name}] returned {items.Count} results for '{query}'");
                            break;
                        }

                        Console.WriteLine($"DuckDuckGo[{strategy.Name}] returned 0 results for '{query}'");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"DuckDuckGo[{strategy.Name}] failed for '{query}': {ex.GetType().Name}: {ex.Message}");
                    }
                }

                stopwatch.Stop();
                response.Items = collected;
                response.ConsumedCount = collected.Count;
                response.ResponseTime = stopwatch.Elapsed;

                if (collected.Count == 0)
                {
                    response.ErrorType = "no_results";
                }
            }
            catch (TaskCanceledException ex)
            {
                stopwatch.Stop();
                response.ResponseTime = stopwatch.Elapsed;
                response.ErrorType = "timeout";
                Console.WriteLine($"DuckDuckGo search timeout: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                response.ResponseTime = stopwatch.Elapsed;
                response.ErrorType = "http_error";
                Console.WriteLine($"DuckDuckGo HTTP error: {ex.Message}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                response.ResponseTime = stopwatch.Elapsed;
                response.ErrorType = "unknown";
                Console.WriteLine($"DuckDuckGo search error: {ex.Message}");
            }

            if (string.IsNullOrEmpty(response.ErrorType))
            {
                StoreCachedSearchResponse(cacheKey, response);
            }

            return response;
        }

        public async Task<List<BingImageResult>> SearchImagesAsync(string query, int startIndex = 1, int limit = 30)
        {
            return (await SearchImagesDetailedAsync(query, startIndex, limit)).Items;
        }

        // ------------------------------------------------------------------
        // Стратегия 1: vqd из HTML + /i.js
        // ------------------------------------------------------------------
        private async Task<List<BingImageResult>> FetchViaVqdJsonAsync(string query, int page, int limit, bool isGif)
        {
            string? vqd = await GetVqdAsync(query);
            if (string.IsNullOrEmpty(vqd))
            {
                return new List<BingImageResult>();
            }

            string url = "https://duckduckgo.com/i.js" +
                         $"?l=us-en&o=json&q={Uri.EscapeDataString(query)}&vqd={vqd}&f=,,,&p={page}";

            string body = await GetStringAsync(url, "https://duckduckgo.com/", "application/json, text/javascript, */*; q=0.01", true);
            var items = ParseIjsJson(body, isGif, limit);

            if (items.Count == 0 && page > 1)
            {
                // Страница могла закончиться — повторяем с первой.
                string firstUrl = "https://duckduckgo.com/i.js" +
                                  $"?l=us-en&o=json&q={Uri.EscapeDataString(query)}&vqd={vqd}&f=,,,&p=1";
                string firstBody = await GetStringAsync(firstUrl, "https://duckduckgo.com/", "application/json, text/javascript, */*; q=0.01", true);
                items = ParseIjsJson(firstBody, isGif, limit);
            }

            return items;
        }

        // ------------------------------------------------------------------
        // Стратегия 2: vqd из /js/vqd.js
        // ------------------------------------------------------------------
        private async Task<List<BingImageResult>> FetchViaVqdJsAsync(string query, int page, int limit, bool isGif)
        {
            string vqdUrl = "https://duckduckgo.com/js/vqd.js?q=" + Uri.EscapeDataString(query) + "&ia=images";
            string body = await GetStringAsync(vqdUrl, "https://duckduckgo.com/", "*/*", false);
            var match = Regex.Match(body, @"vqd\s*=\s*[""']?([0-9]-[0-9a-zA-Z]+)", RegexOptions.None, RegexTimeout);
            if (!match.Success)
            {
                return new List<BingImageResult>();
            }

            string vqd = match.Groups[1].Value;
            StoreVqd(query, vqd);

            string url = "https://duckduckgo.com/i.js" +
                         $"?l=us-en&o=json&q={Uri.EscapeDataString(query)}&vqd={vqd}&f=,,,&p={page}";
            string json = await GetStringAsync(url, "https://duckduckgo.com/", "application/json, text/javascript, */*; q=0.01", true);
            return ParseIjsJson(json, isGif, limit);
        }

        // ------------------------------------------------------------------
        // Стратегия 3: HTML-выдача html.duckduckgo.com
        // ------------------------------------------------------------------
        private async Task<List<BingImageResult>> FetchViaHtmlAsync(string query, int page, int limit, bool isGif)
        {
            var results = new List<BingImageResult>();
            var seen = new HashSet<string>();

            string url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            if (isGif)
            {
                url += "+gif";
            }

            string html = await GetStringAsync(url, "https://html.duckduckgo.com/", "text/html,application/xhtml+xml", false);
            if (string.IsNullOrEmpty(html))
            {
                return results;
            }

            // html.duckduckgo.com отдаёт редирект-ссылки вида //duckduckgo.com/l/?uddg=<encoded>&rut=...
            foreach (Match m in Regex.Matches(html, @"uddg=([^&""']+)", RegexOptions.None, RegexTimeout))
            {
                string decoded = WebUtility.UrlDecode(m.Groups[1].Value);
                if (!decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                AddHtmlResult(results, seen, decoded, decoded, limit);
                if (results.Count >= limit) break;
            }

            // Прямые ссылки на изображения, если они есть в разметке.
            if (results.Count < limit)
            {
                foreach (Match m in Regex.Matches(html,
                    @"https?://[^""'\s<>\\]+?\.(?:jpe?g|png|gif|webp)(?:\?[^""'\s<>\\]*)?",
                    RegexOptions.IgnoreCase, RegexTimeout))
                {
                    string candidate = WebUtility.HtmlDecode(m.Value).Replace("\\/", "/");
                    AddHtmlResult(results, seen, candidate, candidate, limit);
                    if (results.Count >= limit) break;
                }
            }

            return results;
        }

        private static void AddHtmlResult(List<BingImageResult> results, HashSet<string> seen,
            string imageUrl, string sourceUrl, int limit)
        {
            if (results.Count >= limit) return;
            if (string.IsNullOrWhiteSpace(imageUrl)) return;
            if (imageUrl.Contains("<") || imageUrl.Contains(">") || imageUrl.Contains("\"")) return;
            if (imageUrl.Contains("duckduckgo.com")) return;

            string hash = GetImageHash(imageUrl);
            if (!seen.Add(hash)) return;

            bool isGif = imageUrl.ToLowerInvariant().Split('?')[0].EndsWith(".gif");
            results.Add(new BingImageResult
            {
                Url = imageUrl,
                ThumbnailUrl = imageUrl,
                SourceUrl = sourceUrl,
                Id = hash,
                IsGif = isGif
            });
        }

        // ------------------------------------------------------------------
        // Стратегия 4: встроенный JSON в HTML главной страницы
        // ------------------------------------------------------------------
        private async Task<List<BingImageResult>> FetchViaEmbeddedJsonAsync(string query, int page, int limit, bool isGif)
        {
            string url = "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query) + "&iax=images&ia=images";
            string html = await GetStringAsync(url, "https://duckduckgo.com/", "text/html,application/xhtml+xml", false);
            if (string.IsNullOrEmpty(html))
            {
                return new List<BingImageResult>();
            }

            var vqdMatch = Regex.Match(html, @"vqd\s*[:=]\s*[""']?([0-9]-[0-9a-zA-Z]+)", RegexOptions.None, RegexTimeout);
            if (vqdMatch.Success)
            {
                StoreVqd(query, vqdMatch.Groups[1].Value);
            }

            var results = new List<BingImageResult>();
            var seen = new HashSet<string>();

            foreach (Match m in Regex.Matches(html, @"""image""\s*:\s*""(https?:\\?/\\?/[^""]+)""", RegexOptions.None, RegexTimeout))
            {
                string imageUrl = m.Groups[1].Value.Replace("\\/", "/");
                string hash = GetImageHash(imageUrl);
                if (!seen.Add(hash)) continue;

                results.Add(new BingImageResult
                {
                    Url = imageUrl,
                    ThumbnailUrl = imageUrl,
                    SourceUrl = imageUrl,
                    Id = hash,
                    IsGif = imageUrl.ToLowerInvariant().Split('?')[0].EndsWith(".gif")
                });

                if (results.Count >= limit) break;
            }

            return results;
        }

        // ------------------------------------------------------------------
        // Парсинг ответа /i.js
        // ------------------------------------------------------------------
        private static List<BingImageResult> ParseIjsJson(string body, bool isGif, int limit)
        {
            var results = new List<BingImageResult>();
            if (string.IsNullOrWhiteSpace(body))
            {
                return results;
            }

            string json = body.Trim();
            if (!json.StartsWith("{") && !json.StartsWith("["))
            {
                int brace = json.IndexOf('{');
                if (brace < 0) return results;
                json = json.Substring(brace);
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                var seen = new HashSet<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    string image = GetJsonString(item, "image");
                    if (string.IsNullOrEmpty(image)) continue;
                    if (!image.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

                    string thumbnail = GetJsonString(item, "thumbnail");
                    if (string.IsNullOrEmpty(thumbnail)) thumbnail = image;

                    string source = GetJsonString(item, "url");
                    if (string.IsNullOrEmpty(source)) source = image;

                    string title = GetJsonString(item, "title");
                    bool gif = isGif || GetJsonString(item, "type").Contains("gif", StringComparison.OrdinalIgnoreCase) ||
                               image.ToLowerInvariant().Split('?')[0].EndsWith(".gif");

                    string hash = GetImageHash(image);
                    if (!seen.Add(hash)) continue;

                    results.Add(new BingImageResult
                    {
                        Url = image,
                        ThumbnailUrl = thumbnail,
                        SourceUrl = source,
                        Id = hash,
                        IsGif = gif
                    });

                    if (results.Count >= limit) break;
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"DuckDuckGo JSON parse error: {ex.Message}");
            }

            return results;
        }

        private static string GetJsonString(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value)) return string.Empty;
            return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty) : string.Empty;
        }

        // ------------------------------------------------------------------
        // VQD-токен
        // ------------------------------------------------------------------
        private async Task<string?> GetVqdAsync(string query)
        {
            lock (VqdLock)
            {
                if (VqdCache.TryGetValue(query, out var cached) && DateTime.UtcNow < cached.ExpiresAtUtc)
                {
                    return cached.Vqd;
                }
            }

            string url = "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query) + "&iax=images&ia=images";
            string html = await GetStringAsync(url, "https://duckduckgo.com/", "text/html,application/xhtml+xml", false);

            var match = Regex.Match(html, @"vqd\s*[:=]\s*[""']?([0-9]-[0-9a-zA-Z]+)", RegexOptions.None, RegexTimeout);
            if (!match.Success)
            {
                return null;
            }

            string vqd = match.Groups[1].Value;
            StoreVqd(query, vqd);
            return vqd;
        }

        private static void StoreVqd(string query, string vqd)
        {
            lock (VqdLock)
            {
                VqdCache[query] = new CachedVqd
                {
                    ExpiresAtUtc = DateTime.UtcNow.Add(VqdCacheTtl),
                    Vqd = vqd
                };
            }
        }

        // ------------------------------------------------------------------
        // HTTP
        // ------------------------------------------------------------------
        private static async Task<string> GetStringAsync(string url, string referer, string accept, bool xhr)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Referer", referer);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", xhr ? "empty" : "document");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", xhr ? "cors" : "navigate");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            if (xhr)
            {
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            }

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"DuckDuckGo {(int)response.StatusCode} for {new Uri(url).AbsolutePath}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        // ------------------------------------------------------------------
        // Проверка доступности
        // ------------------------------------------------------------------
        public async Task<(bool Ok, string Status)> CheckDuckDuckGoAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://duckduckgo.com/");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                using var response = await _httpClient.SendAsync(request, cts.Token);
                return (response.IsSuccessStatusCode, ((int)response.StatusCode).ToString());
            }
            catch (Exception ex)
            {
                return (false, ex.GetType().Name);
            }
        }

        // ------------------------------------------------------------------
        // Утилиты
        // ------------------------------------------------------------------
        private static bool TryParseGifFlag(ref string query)
        {
            var match = Regex.Match(query, @"(^|\s)--gif(\s|$)", RegexOptions.IgnoreCase, RegexTimeout);
            if (!match.Success) return false;

            query = Regex.Replace(query, @"(^|\s)--gif(\s|$)", " ", RegexOptions.IgnoreCase, RegexTimeout).Trim();
            return true;
        }

        private static string GetImageHash(string url)
        {
            try
            {
                string cleanUrl = url.Split('?')[0].Split('#')[0].ToLowerInvariant().Trim();
                return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(cleanUrl))).ToLowerInvariant();
            }
            catch
            {
                return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
            }
        }

        private static string BuildSearchCacheKey(string query, int page, int limit, bool isGif)
        {
            return $"{query.Trim().ToLowerInvariant()}|{(isGif ? "gif" : "img")}|{page}|{limit}";
        }

        private static bool TryGetCachedSearchResponse(string cacheKey, out BingSearchResponse response)
        {
            lock (SearchCacheLock)
            {
                if (SearchCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAtUtc)
                {
                    response = CloneSearchResponse(cached.Response);
                    return true;
                }

                SearchCache.Remove(cacheKey);
            }

            response = new BingSearchResponse();
            return false;
        }

        private static void StoreCachedSearchResponse(string cacheKey, BingSearchResponse response)
        {
            lock (SearchCacheLock)
            {
                SearchCache[cacheKey] = new CachedSearchResponse
                {
                    ExpiresAtUtc = DateTime.UtcNow.Add(SearchCacheTtl),
                    Response = CloneSearchResponse(response)
                };
            }
        }

        private static BingSearchResponse CloneSearchResponse(BingSearchResponse response)
        {
            return new BingSearchResponse
            {
                ConsumedCount = response.ConsumedCount,
                ResponseTime = response.ResponseTime,
                ErrorType = response.ErrorType,
                Items = response.Items
                    .Select(item => new BingImageResult
                    {
                        Url = item.Url,
                        ThumbnailUrl = item.ThumbnailUrl,
                        SourceUrl = item.SourceUrl,
                        Id = item.Id,
                        IsGif = item.IsGif
                    })
                    .ToList()
            };
        }

        private class CachedSearchResponse
        {
            public DateTime ExpiresAtUtc { get; set; }
            public BingSearchResponse Response { get; set; } = new BingSearchResponse();
        }

        private class CachedVqd
        {
            public DateTime ExpiresAtUtc { get; set; }
            public string Vqd { get; set; } = string.Empty;
        }
    }
}