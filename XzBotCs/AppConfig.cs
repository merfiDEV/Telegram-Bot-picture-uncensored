using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XzBotCs
{
    public class AppConfig
    {
        public string BotToken { get; set; } = string.Empty;
        public HashSet<long> AdminIds { get; set; } = new HashSet<long>();
        public long? CacheChatId { get; set; }
        public string? ProxyBaseUrl { get; set; }
        public int ProxyPort { get; set; } = DefaultProxyPort;

        public const int DefaultProxyPort = 8080;
        public const string DefaultProxyBaseUrl = "http://46.229.63.243:8080/img?u=";
        public const string DeveloperProfileUrl = "https://t.me/Tyta_Zdesyaa777";

        public static AppConfig Load()
        {
            string? token = Environment.GetEnvironmentVariable("BOT_TOKEN");
            string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_ID");
            string? cacheChatIdStr = Environment.GetEnvironmentVariable("CACHE_CHAT_ID");
            string? proxyBaseUrl = Environment.GetEnvironmentVariable("PROXY_BASE_URL")
                ?? Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
            string? proxyPortStr = Environment.GetEnvironmentVariable("PROXY_PORT");

            if (File.Exists("../.env"))
            {
                var lines = File.ReadAllLines("../.env");
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
                throw new InvalidOperationException("BOT_TOKEN not found.");
            }

            var adminIds = new HashSet<long>();
            if (!string.IsNullOrEmpty(adminIdStr))
            {
                foreach (var part in adminIdStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (long.TryParse(part, out long aid)) adminIds.Add(aid);
                }
            }

            long? cacheChatId = null;
            if (long.TryParse(cacheChatIdStr, out long cid)) cacheChatId = cid;
            else cacheChatId = adminIds.Count > 0 ? adminIds.First() : null;

            int proxyPort = int.TryParse(proxyPortStr, out int parsedProxyPort) ? parsedProxyPort : DefaultProxyPort;

            return new AppConfig
            {
                BotToken = token,
                AdminIds = adminIds,
                CacheChatId = cacheChatId,
                ProxyBaseUrl = NormalizeProxyBaseUrl(proxyBaseUrl ?? DefaultProxyBaseUrl),
                ProxyPort = proxyPort
            };
        }

        private static string ReadEnvValue(string line, string key)
        {
            return line.Substring(key.Length).Trim().Trim('"').Trim('\'');
        }

        private static string? NormalizeProxyBaseUrl(string? proxyBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(proxyBaseUrl)) return null;

            proxyBaseUrl = proxyBaseUrl.Trim();
            return proxyBaseUrl.Contains("?u=", StringComparison.OrdinalIgnoreCase)
                ? proxyBaseUrl
                : $"{proxyBaseUrl.TrimEnd('/')}/img?u=";
        }
    }
}
