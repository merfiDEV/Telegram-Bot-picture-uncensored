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
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
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
            bool isGifSearch = TryParseGifFlag(ref query);

            string cacheKey = BuildSearchCacheKey(query, startIndex, limit, isGifSearch);
            if (TryGetCachedSearchResponse(cacheKey, out var cachedResponse))
            {
                return cachedResponse;
            }

            var searchResponse = new BingSearchResponse();
            var results = searchResponse.Items;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (isGifSearch)
                {
                    var filtered = await FetchPageAsync(query, startIndex, limit, "%2Bfilterui%3Aphoto-animatedgif");
                    results.AddRange(filtered);

                    if (results.Count == 0)
                    {
                        Console.WriteLine($"GIF search returned 0 with filter, trying fallback for '{query}'");
                        var plain = await FetchPageAsync(query, startIndex, limit, null);
                        foreach (var item in plain)
                        {
                            if (item.Url.ToLowerInvariant().Split('?')[0].EndsWith(".gif"))
                            {
                                item.IsGif = true;
                                results.Add(item);
                                if (results.Count >= limit) break;
                            }
                        }
                    }
                }
                else
                {
                    var fetched = await FetchPageAsync(query, startIndex, limit, null);
                    results.AddRange(fetched);
                }

                stopwatch.Stop();
                searchResponse.ResponseTime = stopwatch.Elapsed;
                searchResponse.ConsumedCount = results.Count;
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

        private async Task<List<BingImageResult>> FetchPageAsync(string query, int startIndex, int limit, string? qftFilter)
        {
            var results = new List<BingImageResult>();

            string encodedQuery = HttpUtility.UrlEncode(query);
            string fetchUrl = $"https://www.bing.com/images/search?q={encodedQuery}&adlt=off&safeSearch=Off&setmkt=en-US&setlang=en-US&first={startIndex}&count={limit}";
            if (!string.IsNullOrEmpty(qftFilter))
            {
                fetchUrl += $"&qft={qftFilter}";
            }

            var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
            request.Headers.Add("Cookie", "SRCHHPGUSR=ADLT=OFF&NRSLT=50");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string html = await response.Content.ReadAsStringAsync();
            var blocks = Regex.Matches(html, @"m=""({.*?})""", RegexOptions.None, RegexTimeout);
            var seenHashes = new HashSet<string>();
            bool gifByFilter = !string.IsNullOrEmpty(qftFilter);

            foreach (Match blockMatch in blocks)
            {
                string block = blockMatch.Groups[1].Value;

                var murlMatch = Regex.Match(block, @"murl&quot;:&quot;(.*?)&quot;", RegexOptions.None, RegexTimeout);
                var turlMatch = Regex.Match(block, @"turl&quot;:&quot;(.*?)&quot;", RegexOptions.None, RegexTimeout);
                var purlMatch = Regex.Match(block, @"purl&quot;:&quot;(.*?)&quot;", RegexOptions.None, RegexTimeout);

                if (!murlMatch.Success) continue;

                string murl = HttpUtility.HtmlDecode(murlMatch.Groups[1].Value).Replace("\\/", "/");
                string turl = turlMatch.Success ? HttpUtility.HtmlDecode(turlMatch.Groups[1].Value).Replace("\\/", "/") : murl;
                string purl = purlMatch.Success ? HttpUtility.HtmlDecode(purlMatch.Groups[1].Value).Replace("\\/", "/") : string.Empty;

                if (!murl.StartsWith("http")) continue;
                if (murl.Contains("<") || murl.Contains(">") || murl.Contains("\"") || murl.Contains(" ")) continue;

                bool isGif = gifByFilter || murl.ToLowerInvariant().Split('?')[0].EndsWith(".gif");
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

            return results;
        }

        private static bool TryParseGifFlag(ref string query)
        {
            var match = Regex.Match(query, @"(^|\s)--gif(\s|$)", RegexOptions.IgnoreCase, RegexTimeout);
            if (!match.Success) return false;

            query = Regex.Replace(query, @"(^|\s)--gif(\s|$)", " ", RegexOptions.IgnoreCase, RegexTimeout).Trim();
            return true;
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

        private static string BuildSearchCacheKey(string query, int startIndex, int limit, bool isGif)
        {
            return $"{query.Trim().ToLowerInvariant()}|{(isGif ? "gif" : "img")}|{startIndex}|{limit}";
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
    }
}