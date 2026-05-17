using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace XzBotCs.Services
{
    public class BingImageResult
    {
        public string Url { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public bool IsGif { get; set; }
    }

    public class BingSearchResponse
    {
        public List<BingImageResult> Items { get; set; } = new List<BingImageResult>();
        public int ConsumedCount { get; set; }
        public TimeSpan ResponseTime { get; set; }
        public string? ErrorType { get; set; }
    }

    public class BingSearchService
    {
        private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler { UseCookies = true });
        private static readonly TimeSpan ImageMetadataTimeout = TimeSpan.FromMilliseconds(650);
        private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(18);
        private static readonly object SearchCacheLock = new object();
        private static readonly Dictionary<string, CachedSearchResponse> SearchCache = new Dictionary<string, CachedSearchResponse>();

        static BingSearchService()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        }

        public async Task<List<BingImageResult>> SearchImagesAsync(string query, int startIndex = 1, int limit = 30)
        {
            return (await SearchImagesDetailedAsync(query, startIndex, limit)).Items;
        }

        public async Task<BingSearchResponse> SearchImagesDetailedAsync(string query, int startIndex = 1, int limit = 30)
        {
            string cacheKey = BuildSearchCacheKey(query, startIndex, limit);
            if (TryGetCachedSearchResponse(cacheKey, out var cachedResponse))
            {
                return cachedResponse;
            }

            var searchResponse = new BingSearchResponse();
            var results = searchResponse.Items;
            string bingFilters = "";
            bool isGifSearch = false;

            if (query.Contains("--gif"))
            {
                isGifSearch = true;
                query = query.Replace("--gif", "").Trim();
                bingFilters = "+filterui:photo-animatedgif";
            }

            string encodedQuery = HttpUtility.UrlEncode(query);
            string fetchUrl = $"https://www.bing.com/images/search?q={encodedQuery}&adlt=off&safeSearch=Off&setmkt=en-US&setlang=en-US&first={startIndex}";
            if (!string.IsNullOrEmpty(bingFilters))
            {
                fetchUrl += $"&qft={bingFilters}";
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
                request.Headers.Add("Cookie", "SRCHHPGUSR=ADLT=OFF&NRSLT=50");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                var stopwatch = Stopwatch.StartNew();
                var response = await _httpClient.SendAsync(request);
                stopwatch.Stop();
                searchResponse.ResponseTime = stopwatch.Elapsed;
                response.EnsureSuccessStatusCode();

                string html = await response.Content.ReadAsStringAsync();
                var blocks = Regex.Matches(html, @"m=""({.*?})""");
                searchResponse.ConsumedCount = blocks.Count;
                var seenHashes = new HashSet<string>();

                foreach (Match blockMatch in blocks)
                {
                    string block = blockMatch.Groups[1].Value;
                    
                    var murlMatch = Regex.Match(block, @"murl&quot;:&quot;(.*?)&quot;");
                    var turlMatch = Regex.Match(block, @"turl&quot;:&quot;(.*?)&quot;");
                    var purlMatch = Regex.Match(block, @"purl&quot;:&quot;(.*?)&quot;");

                    if (!murlMatch.Success) continue;

                    string murl = HttpUtility.HtmlDecode(murlMatch.Groups[1].Value).Replace("\\/", "/");
                    string turl = turlMatch.Success ? HttpUtility.HtmlDecode(turlMatch.Groups[1].Value).Replace("\\/", "/") : murl;
                    string purl = purlMatch.Success ? HttpUtility.HtmlDecode(purlMatch.Groups[1].Value).Replace("\\/", "/") : string.Empty;

                    if (!murl.StartsWith("http")) continue;
                    if (murl.Contains("<") || murl.Contains(">") || murl.Contains("\"") || murl.Contains(" ")) continue;

                    bool isGif = isGifSearch || murl.ToLowerInvariant().Split('?')[0].EndsWith(".gif");
                    string imageHash = GetImageHash(murl);
                    if (!seenHashes.Add(imageHash)) continue;

                    results.Add(new BingImageResult
                    {
                        Url = murl,
                        ThumbnailUrl = turl,
                        SourceUrl = purl,
                        Id = imageHash,
                        IsGif = isGif
                    });

                    if (results.Count >= limit) break;
                }
            }
            catch (TaskCanceledException ex)
            {
                searchResponse.ErrorType = "timeout";
                Console.WriteLine($"Search timeout: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                searchResponse.ErrorType = "http_error";
                Console.WriteLine($"Search HTTP error: {ex.Message}");
            }
            catch (RegexMatchTimeoutException ex)
            {
                searchResponse.ErrorType = "parse_error";
                Console.WriteLine($"Search parse error: {ex.Message}");
            }
            catch (Exception ex)
            {
                searchResponse.ErrorType = "unknown";
                Console.WriteLine($"Search error: {ex.Message}");
            }

            if (string.IsNullOrEmpty(searchResponse.ErrorType))
            {
                StoreCachedSearchResponse(cacheKey, searchResponse);
            }

            return searchResponse;
        }

        public async Task<(bool Ok, string Status)> CheckBingAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var response = await _httpClient.GetAsync("https://www.bing.com", cts.Token);
                return (response.IsSuccessStatusCode, ((int)response.StatusCode).ToString());
            }
            catch (Exception ex)
            {
                return (false, ex.GetType().Name);
            }
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

        private static string BuildSearchCacheKey(string query, int startIndex, int limit)
        {
            return $"{query.Trim().ToLowerInvariant()}|{startIndex}|{limit}";
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

        private static async Task<(bool IsValid, bool IsGif)> IsValidImageAsync(string url)
        {
            var head = await RequestImageMetadataAsync(HttpMethod.Head, url);
            if (head.Response != null)
            {
                using (head.Response)
                {
                    if (head.Response.IsSuccessStatusCode)
                    {
                        var contentType = head.Response.Content.Headers.ContentType?.MediaType;
                        if (!string.IsNullOrWhiteSpace(contentType))
                        {
                            return ParseImageResponse(url, contentType);
                        }
                    }
                    else if ((int)head.Response.StatusCode != 403 && (int)head.Response.StatusCode != 405)
                    {
                        return (false, false);
                    }
                }
            }

            foreach (var headers in new[] { "bytes=0-0" })
            {
                var get = await RequestImageMetadataAsync(HttpMethod.Get, url, headers);
                if (get.Response == null) continue;

                using (get.Response)
                {
                    if (!get.Response.IsSuccessStatusCode) continue;

                    var contentType = get.Response.Content.Headers.ContentType?.MediaType;
                    if (!string.IsNullOrWhiteSpace(contentType))
                    {
                        return ParseImageResponse(url, contentType);
                    }
                }
            }

            return (false, false);
        }

        private static async Task<(HttpResponseMessage? Response, Exception? Error)> RequestImageMetadataAsync(HttpMethod method, string url, string? range = null)
        {
            try
            {
                using var cts = new CancellationTokenSource(ImageMetadataTimeout);
                using var request = new HttpRequestMessage(method, url);
                if (range != null)
                {
                    request.Headers.TryAddWithoutValidation("Range", range);
                }

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return (response, null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }

        private static (bool IsValid, bool IsGif) ParseImageResponse(string url, string contentType)
        {
            contentType = contentType.ToLowerInvariant().Trim();
            bool isValid = contentType.StartsWith("image/");
            bool isGif = contentType == "image/gif";

            if (!isGif && url.ToLowerInvariant().Split('?')[0].EndsWith(".gif"))
            {
                isGif = true;
            }

            return (isValid, isGif);
        }
    }
}
