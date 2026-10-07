using System;
using System.Text.RegularExpressions;

namespace XzBotCs.Helpers
{
    /// <summary>
    /// Разбор пользовательского ввода: флаги, ID, URL прокси.
    /// Чистые функции без состояния.
    /// </summary>
    public static class UserInputHelper
    {
        /// <summary>
        /// Ищет флаг (например "--gif") как отдельное слово в запросе.
        /// При нахождении удаляет его из <paramref name="query"/> и возвращает true.
        /// </summary>
        public static bool TryParseFlag(ref string query, string flag, TimeSpan regexTimeout)
        {
            var pattern = $@"(^|\s){Regex.Escape(flag)}(\s|$)";
            var match = Regex.Match(query, pattern, RegexOptions.IgnoreCase, regexTimeout);
            if (!match.Success) return false;

            query = Regex.Replace(query, pattern, " ", RegexOptions.IgnoreCase, regexTimeout).Trim();
            return true;
        }

        /// <summary>
        /// Извлекает Telegram user ID из строки: поддерживает чистый ID и ссылку tg://user?id=...
        /// Возвращает 0, если ID не найден.
        /// </summary>
        public static long ExtractUserId(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return 0;

            input = input.Trim();

            if (long.TryParse(input, out long directId)) return directId;

            const string marker = "user?id=";
            int index = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                string idStr = input.Substring(index + marker.Length);
                int end = idStr.IndexOfAny(new[] { '&', '?', ' ', '\t' });
                if (end >= 0) idStr = idStr.Substring(0, end);
                if (long.TryParse(idStr, out long tgId)) return tgId;
            }

            return 0;
        }

        /// <summary>
        /// Приводит базовый URL прокси к виду ".../img?u=". Пустой вход → null.
        /// </summary>
        public static string? NormalizeProxyBaseUrl(string? proxyBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(proxyBaseUrl)) return null;

            proxyBaseUrl = proxyBaseUrl.Trim();
            return proxyBaseUrl.Contains("?u=", StringComparison.OrdinalIgnoreCase)
                ? proxyBaseUrl
                : $"{proxyBaseUrl.TrimEnd('/')}/img?u=";
        }
    }
}
