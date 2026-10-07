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
                    if (message.Chat.Type == ChatType.Private && message.From != null)
                    {
                        bool added;
                        lock (_state.SyncRoot)
                        {
                            added = _state.Subscribers.Add(message.From.Id);
                        }
                        if (added) _state.Save();
                    }

                    // Обработка ввода поиска для дашборда
                    if (message.Chat.Type == ChatType.Private && message.From != null && !messageText.StartsWith("/") &&
                        _state.DashboardStates.TryGetValue(message.From.Id, out var pendingSearchState) && pendingSearchState.AwaitingSearch)
                    {
                        pendingSearchState.AwaitingSearch = false;
                        string dashText = _statsService.BuildDashboardText(message.From.Id, page: 0, search: messageText.Trim());
                        var dashMarkup = _statsService.BuildDashboardMarkup(message.From.Id);
                        var dashButtons2 = dashMarkup.InlineKeyboard.ToList();
                        dashButtons2.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "stats:back") });
                        dashMarkup = new InlineKeyboardMarkup(dashButtons2);
                        try
                        {
                            await botClient.SendMessage(message.Chat.Id, dashText, parseMode: ParseMode.MarkdownV2, replyMarkup: dashMarkup, cancellationToken: cancellationToken);
                        }
                        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("can't parse entities"))
                        {
                            Console.WriteLine($"Dashboard search markdown failed, fallback: {ex.Message}");
                            await botClient.SendMessage(message.Chat.Id, dashText, replyMarkup: dashMarkup, cancellationToken: cancellationToken);
                        }
                        return;
                    }

                    if (messageText.StartsWith("/start"))
                    {
                        if (messageText.Contains("register") && message.Chat.Type == ChatType.Private)
                        {
                            lock (_state.SyncRoot)
                            {
                                _state.Subscribers.Add(message.From!.Id);
                            }
                            _state.Save();
                            var registerMarkup = new InlineKeyboardMarkup(InlineKeyboardButton.WithSwitchInlineQueryCurrentChat("🔍 Начать использовать", ""));
                            await botClient.SendMessage(
                                message.Chat.Id,
                                "✅ *Регистрация подтверждена\\!*\n\nТеперь вы можете пользоваться ботом в inline\\-режиме в любом чате\\.",
                                parseMode: ParseMode.MarkdownV2,
                                replyMarkup: registerMarkup,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        if (messageText.Contains("developer"))
                        {
                            var devBtn = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("💻 Открыть профиль", DeveloperProfileUrl));
                            await botClient.SendMessage(message.Chat.Id, "💻 *Профиль разработчика*", parseMode: ParseMode.Markdown, replyMarkup: devBtn, cancellationToken: cancellationToken);
                            return;
                        }

                        string text = "*🤖 Бот работает в асинхронном inline режиме!*\n\n" +
                                     "Чтобы использовать бота, откройте любой чат и введите:\n" +
                                     "`@имя_бота ваш_запрос`\n\n" +
                                     "⚡ Используйте флаг `--gif` для анимаций и `--random` для случайной картинки.\n" +
                                     "🎲 Команда `/random тема` — «мне повезёт»: одна случайная картинка по теме.\n\n" +
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
                    else if (messageText == "/help")
                    {
                        string helpText =
                            "*📖 Справка*\n\n" +
                            "Бот ищет картинки через inline\\-режим\\. Введите `@имя_бота запрос` в любом чате\\.\n\n" +
                            "*Флаги поиска:*\n" +
                            "— `--gif` — искать анимации\n" +
                            "— `--random` — одна случайная картинка по теме\n\n" +
                            "*Команды:*\n" +
                            "— `/random тема` — «мне повезёт»: случайная картинка\n" +
                            "— `/start` — приветствие\n" +
                            "— `/help` — эта справка\n\n" +
                            "Примеры:\n" +
                            "`@имя_бота котики`\n" +
                            "`@имя_бота dance --gif`\n" +
                            "`@имя_бота cyberpunk --random`\n" +
                            "`/random лес`";
                        await botClient.SendMessage(message.Chat.Id, helpText, parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
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

                        // Ротация: оставляем только 5 самых свежих логов, чтобы архив не превышал лимит Telegram.
                                                var logFiles = new DirectoryInfo(logsPath).GetFiles("logs_*.txt")
                                                    .OrderByDescending(f => f.LastWriteTimeUtc)
                                                    .ToList();
                                                var filesToZip = logFiles.Take(5).ToList();

                                                string zipPath = Path.Combine(Path.GetTempPath(), $"logs_{Guid.NewGuid():N}.zip");
                                                try
                                                {
                                                    using (var fs = new FileStream(zipPath, FileMode.Create))
                                                    using (var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                                                    {
                                                        foreach (var file in filesToZip)
                                                        {
                                                            var entry = archive.CreateEntry(file.Name);
                                                            using var entryStream = entry.Open();
                                                            using var fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                                            fileStream.CopyTo(entryStream);
                                                        }
                                                    }

                            using var stream = System.IO.File.OpenRead(zipPath);
                            await botClient.SendDocument(message.Chat.Id, InputFile.FromStream(stream, "logs.zip"), cancellationToken: cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            await botClient.SendMessage(message.Chat.Id, $"Ошибка при сборе логов: {TelegramEscaper.EscapeMarkdownV2(ex.Message)}", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
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

                        lock (_state.SyncRoot)
                        {
                            _state.WatermarkText = newWatermark;
                            _state.WatermarkFileIds.Clear();
                        }
                        _state.Save();

                        await botClient.SendMessage(
                            message.Chat.Id,
                            $"✅ Текст водяного знака изменен на: `{TelegramEscaper.EscapeMarkdownV2(newWatermark)}`\\.\nКэш старых ватермарок в Telegram очищен\\.",
                            parseMode: ParseMode.MarkdownV2,
                            cancellationToken: cancellationToken);
                    }
                    else if (messageText.StartsWith("/makeadmin"))
                    {
                        if (_adminIds.Count == 0 || message.From?.Id == null || !_adminIds.Contains(message.From.Id))
                        {
                            await botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                            return;
                        }

                        string cmdPrefix = "/makeadmin";
                        if (messageText.StartsWith("/makeadmin@"))
                        {
                            int spaceIndex = messageText.IndexOf(' ');
                            cmdPrefix = spaceIndex >= 0 ? messageText.Substring(0, spaceIndex) : messageText;
                        }

                        string arg = messageText.Substring(cmdPrefix.Length).Trim();
                        long newAdminId = UserInputHelper.ExtractUserId(arg);
                        if (newAdminId == 0)
                        {
                            await botClient.SendMessage(
                                message.Chat.Id,
                                "⚠️ Укажите корректный ID пользователя\\.\nПримеры:\n`/makeadmin 1741079861`\n`/makeadmin tg://user?id=1741079861`",
                                parseMode: ParseMode.MarkdownV2,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        bool added;
                        lock (_state.SyncRoot)
                        {
                            added = _adminIds.Add(newAdminId);
                            _state.ExtraAdmins.Add(newAdminId);
                        }
                        _state.Save();

                        string adminText =
                            "✦ ────────────── ✦\n" +
                            "👑 *Администратор выдан*\n\n" +
                            $"🆔 ID: `{newAdminId}`\n\n" +
                            (added
                                ? "✅ Пользователь получил полный доступ к админ\\-командам\\.\n"
                                : "ℹ️ Пользователь уже был администратором\\.\n") +
                            "✦ ────────────── ✦";

                        await botClient.SendMessage(
                            message.Chat.Id,
                            adminText,
                            parseMode: ParseMode.MarkdownV2,
                            cancellationToken: cancellationToken);
                    }
                    else if (messageText.StartsWith("/unmakeadmin"))
                    {
                        if (_adminIds.Count == 0 || message.From?.Id == null || !_adminIds.Contains(message.From.Id))
                        {
                            await botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                            return;
                        }

                        string cmdPrefix = "/unmakeadmin";
                        if (messageText.StartsWith("/unmakeadmin@"))
                        {
                            int spaceIndex = messageText.IndexOf(' ');
                            cmdPrefix = spaceIndex >= 0 ? messageText.Substring(0, spaceIndex) : messageText;
                        }

                        string arg = messageText.Substring(cmdPrefix.Length).Trim();
                        long removeAdminId = UserInputHelper.ExtractUserId(arg);
                        if (removeAdminId == 0)
                        {
                            await botClient.SendMessage(
                                message.Chat.Id,
                                "⚠️ Укажите корректный ID пользователя\\.\nПримеры:\n`/unmakeadmin 1741079861`\n`/unmakeadmin tg://user?id=1741079861`",
                                parseMode: ParseMode.MarkdownV2,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        bool removed;
                        lock (_state.SyncRoot)
                        {
                            removed = _adminIds.Remove(removeAdminId);
                            _state.ExtraAdmins.Remove(removeAdminId);
                        }
                        _state.Save();

                        string adminText =
                            "✦ ────────────── ✦\n" +
                            "🚫 *Администратор снят*\n\n" +
                            $"🆔 ID: `{removeAdminId}`\n\n" +
                            (removed
                                ? "❌ Пользователь лишён админ\\-доступа\\.\n"
                                : "ℹ️ Пользователь и так не был администратором\\.\n") +
                            "✦ ────────────── ✦";

                        await botClient.SendMessage(
                            message.Chat.Id,
                            adminText,
                            parseMode: ParseMode.MarkdownV2,
                            cancellationToken: cancellationToken);
                    }
                    else if (messageText.StartsWith("/setpref"))
                    {
                        if (_adminIds.Count == 0 || message.From?.Id == null || !_adminIds.Contains(message.From.Id))
                        {
                            await botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                            return;
                        }

                        string cmdPrefix = "/setpref";
                        if (messageText.StartsWith("/setpref@"))
                        {
                            int spaceIndex = messageText.IndexOf(' ');
                            cmdPrefix = spaceIndex >= 0 ? messageText.Substring(0, spaceIndex) : messageText;
                        }

                        string arg = messageText.Substring(cmdPrefix.Length).Trim();
                        if (string.IsNullOrEmpty(arg))
                        {
                            await botClient.SendMessage(
                                message.Chat.Id,
                                "⚠️ Укажите ID и префикс\\.\nПример: `/setpref 1741079861 🅰`\nДля очистки: `/setpref 1741079861`",
                                parseMode: ParseMode.MarkdownV2,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        var parts = arg.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        long targetId = UserInputHelper.ExtractUserId(parts[0]);
                        if (targetId == 0)
                        {
                            await botClient.SendMessage(
                                message.Chat.Id,
                                "⚠️ Неверный ID\\. Пример: `/setpref 1741079861 🅰`",
                                parseMode: ParseMode.MarkdownV2,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        string prefix = parts.Length > 1 ? parts[1].Trim() : string.Empty;

                        lock (_prefs.SyncRoot)
                        {
                            if (string.IsNullOrEmpty(prefix))
                            {
                                _prefs.Prefixes.Remove(targetId);
                            }
                            else
                            {
                                _prefs.Prefixes[targetId] = prefix;
                            }
                        }
                        _prefs.Save();

                        string prefixText =
                            "*— Статус изменён —*\n" +
                            $"ID: `{targetId}`\n" +
                            (string.IsNullOrEmpty(prefix)
                                ? "Префикс снят\\."
                                : $"Префикс: {TelegramEscaper.EscapeMarkdownV2(prefix)}");

                        await botClient.SendMessage(
                            message.Chat.Id,
                            prefixText,
                            parseMode: ParseMode.MarkdownV2,
                            cancellationToken: cancellationToken);
                    }
                    else if (messageText.StartsWith("/provider"))
                                        {
                                            await HandleProviderCommandAsync(botClient, message, messageText, cancellationToken);
                                        }
                                        else if (messageText.StartsWith("/admins"))
                                        {
                                            if (_adminIds.Count == 0 || message.From?.Id == null || !_adminIds.Contains(message.From.Id))
                        {
                            await botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                            return;
                        }

                        var lines = new List<string>
                        {
                            "*Администрация*"
                        };

                        foreach (var adminId in _adminIds.OrderBy(id => id))
                        {
                            string? username = await TryGetUsernameAsync(botClient, adminId, cancellationToken);
                            string display = string.IsNullOrEmpty(username) ? "без username" : $"@{username}";
                            _prefs.Prefixes.TryGetValue(adminId, out string? pref);
                            string prefix = string.IsNullOrEmpty(pref) ? "" : $"{TelegramEscaper.EscapeMarkdownV2(pref)} — ";
                            lines.Add($"{prefix}{TelegramEscaper.EscapeMarkdownV2(display)} — `{adminId}`");
                        }

                        await botClient.SendMessage(
                            message.Chat.Id,
                            string.Join("\n", lines),
                            parseMode: ParseMode.MarkdownV2,
                            cancellationToken: cancellationToken);
                    }
                    else if (messageText.StartsWith("/random"))
                    {
                        string cmdPrefix = "/random";
                        if (messageText.StartsWith("/random@"))
                        {
                            int spaceIndex = messageText.IndexOf(' ');
                            cmdPrefix = spaceIndex >= 0 ? messageText.Substring(0, spaceIndex) : messageText;
                        }

                        string topic = messageText.Substring(cmdPrefix.Length).Trim();
                        if (string.IsNullOrEmpty(topic))
                        {
                            await botClient.SendMessage(
                                message.Chat.Id,
                                "🎲 Укажите тему\\.\nПример: `/random кот`",
                                parseMode: ParseMode.MarkdownV2,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        await SendRandomImageAsync(message.Chat.Id, topic, cancellationToken);
                    }
                    else if (messageText.StartsWith("/upt"))
                    {
                        if (_adminIds.Count == 0 || message.From?.Id == null || !_adminIds.Contains(message.From.Id))
                        {
                            await botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                            return;
                        }

                        string cmdPrefix = "/upt";
                        if (messageText.StartsWith("/upt@"))
                        {
                            int spaceIndex = messageText.IndexOf(' ');
                            cmdPrefix = spaceIndex >= 0 ? messageText.Substring(0, spaceIndex) : messageText;
                        }

                        string announcement = messageText.Substring(cmdPrefix.Length).Trim();
                        if (string.IsNullOrEmpty(announcement))
                        {
                            await botClient.SendMessage(
                                message.Chat.Id,
                                "⚠️ Укажите текст рассылки\\.\nПример: `/upt Важное обновление!`",
                                parseMode: ParseMode.MarkdownV2,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        await SendBroadcastAsync(message.Chat.Id, announcement, cancellationToken);
                    }
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

        static async Task HandleProviderCommandAsync(ITelegramBotClient botClient, Message message, string messageText, CancellationToken ct)
        {
            if (_adminIds.Count == 0 || message.From?.Id == null || !_adminIds.Contains(message.From.Id))
            {
                await botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                return;
            }

            // Первый токен — сама команда (поддерживает /provider@botname). Отрезаем
                        // именно его, а не фиксированную строку "/provider", иначе "/providers ddg"
                        // превращается в аргумент "s ddg".
                        int firstSpace = messageText.IndexOfAny(new[] { ' ', '\t' });
                        string arg = (firstSpace >= 0 ? messageText.Substring(firstSpace + 1) : string.Empty)
                            .Trim().ToLowerInvariant();

                        if (string.IsNullOrEmpty(arg))
                        {
                            var available = string.Join(", ", _searchRegistry.All.Select(p => $"{p.Key} ({p.DisplayName})"));
                            await botClient.SendMessage(
                                message.Chat.Id,
                                $"🔎 *Провайдер поиска*\\nАктивный: `{TelegramEscaper.EscapeMarkdownV2(_searchRegistry.ActiveKey)}`\\nДоступные: {TelegramEscaper.EscapeMarkdownV2(available)}\\n\\nИспользование: `/provider bing` или `/provider ddg`",
                                parseMode: ParseMode.MarkdownV2,
                                cancellationToken: ct);
                            return;
                        }

                        if (_searchRegistry.SetActive(arg))
            {
                await botClient.SendMessage(
                    message.Chat.Id,
                    $"✅ Провайдер переключён на `{TelegramEscaper.EscapeMarkdownV2(_searchRegistry.ActiveKey)}`",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: ct);
            }
            else
            {
                await botClient.SendMessage(
                    message.Chat.Id,
                    $"⚠️ Неизвестный провайдер: `{TelegramEscaper.EscapeMarkdownV2(arg)}`",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: ct);
            }
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

        private static async Task<string?> TryGetUsernameAsync(ITelegramBotClient botClient, long userId, CancellationToken cancellationToken)
        {
            try
            {
                var chat = await botClient.GetChat(new ChatId(userId), cancellationToken: cancellationToken);
                return chat.Username;
            }
            catch
            {
                return null;
            }
        }

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
