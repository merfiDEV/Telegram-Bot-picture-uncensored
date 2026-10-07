using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using XzBotCs.Commands;
using XzBotCs.Helpers;
using XzBotCs.Models;
using XzBotCs.Services;

namespace XzBotCs.Handlers
{
    /// <summary>
    /// Обработка текстовых команд из личных/групповых чатов:
    /// /start, /help, /stats, /logs, /setwatermark, /makeadmin, /unmakeadmin,
    /// /setpref, /provider, /admins, /random, /upt, а также ввод поиска дашборда.
    /// </summary>
    public class MessageCommandHandler
    {
        private readonly ITelegramBotClient _botClient;
        private readonly BotState _state;
        private readonly PrefStore _prefs;
        private readonly HashSet<long> _adminIds;
        private readonly BotStatsService _statsService;
        private readonly BroadcastService _broadcastService;
        private readonly StatsMessageService _statsMessageService;
        private readonly SearchProviderRegistry _searchRegistry;

        public MessageCommandHandler(
            ITelegramBotClient botClient,
            BotState state,
            PrefStore prefs,
            HashSet<long> adminIds,
            BotStatsService statsService,
            BroadcastService broadcastService,
            StatsMessageService statsMessageService,
            SearchProviderRegistry searchRegistry)
        {
            _botClient = botClient;
            _state = state;
            _prefs = prefs;
            _adminIds = adminIds;
            _statsService = statsService;
            _broadcastService = broadcastService;
            _statsMessageService = statsMessageService;
            _searchRegistry = searchRegistry;
        }

        /// <summary>
        /// Обрабатывает текстовое сообщение. Возвращает true, если сообщение
        /// обработано как команда/ввод дашборда.
        /// </summary>
        public async Task<bool> HandleAsync(Message message, string messageText, CancellationToken cancellationToken)
        {
            // Регистрация подписчика при первом сообщении в ЛС.
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
                    await _botClient.SendMessage(message.Chat.Id, dashText, parseMode: ParseMode.MarkdownV2, replyMarkup: dashMarkup, cancellationToken: cancellationToken);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("can't parse entities"))
                {
                    Console.WriteLine($"Dashboard search markdown failed, fallback: {ex.Message}");
                    await _botClient.SendMessage(message.Chat.Id, dashText, replyMarkup: dashMarkup, cancellationToken: cancellationToken);
                }
                return true;
            }

            if (messageText.StartsWith("/start"))
            {
                await HandleStartAsync(message, messageText, cancellationToken);
            }
            else if (messageText == "/help")
            {
                await HandleHelpAsync(message, cancellationToken);
            }
            else if (messageText == "/stats")
            {
                if (!IsAdmin(message.From?.Id))
                {
                    await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                    return true;
                }
                await _statsMessageService.SendStatsAsync(message.Chat.Id, cancellationToken);
            }
            else if (messageText == "/logs")
            {
                await HandleLogsAsync(message, cancellationToken);
            }
            else if (messageText.StartsWith("/setwatermark"))
            {
                await HandleSetWatermarkAsync(message, messageText, cancellationToken);
            }
            else if (messageText.StartsWith("/makeadmin"))
            {
                await HandleMakeAdminAsync(message, messageText, cancellationToken);
            }
            else if (messageText.StartsWith("/unmakeadmin"))
            {
                await HandleUnmakeAdminAsync(message, messageText, cancellationToken);
            }
            else if (messageText.StartsWith("/setpref"))
            {
                await HandleSetPrefAsync(message, messageText, cancellationToken);
            }
            else if (messageText.StartsWith("/provider"))
            {
                await HandleProviderCommandAsync(message, messageText, cancellationToken);
            }
            else if (messageText.StartsWith("/admins"))
            {
                await HandleAdminsAsync(message, cancellationToken);
            }
            else if (messageText.StartsWith("/random"))
            {
                await HandleRandomAsync(message, messageText, cancellationToken);
            }
            else if (messageText.StartsWith("/upt"))
            {
                await HandleBroadcastCommandAsync(message, messageText, cancellationToken);
            }
            else
            {
                return false;
            }

            return true;
        }

        private bool IsAdmin(long? userId)
            => userId.HasValue && (_adminIds.Count == 0 || _adminIds.Contains(userId.Value));

        private async Task HandleStartAsync(Message message, string messageText, CancellationToken cancellationToken)
        {
            if (messageText.Contains("register") && message.Chat.Type == ChatType.Private)
            {
                lock (_state.SyncRoot)
                {
                    _state.Subscribers.Add(message.From!.Id);
                }
                _state.Save();
                var registerMarkup = new InlineKeyboardMarkup(InlineKeyboardButton.WithSwitchInlineQueryCurrentChat("🔍 Начать использовать", ""));
                await _botClient.SendMessage(
                    message.Chat.Id,
                    "✅ *Регистрация подтверждена\\!*\n\nТеперь вы можете пользоваться ботом в inline\\-режиме в любом чате\\.",
                    parseMode: ParseMode.MarkdownV2,
                    replyMarkup: registerMarkup,
                    cancellationToken: cancellationToken);
                return;
            }

            if (messageText.Contains("developer"))
            {
                var devBtn = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("💻 Открыть профиль", AppConfig.DeveloperProfileUrl));
                await _botClient.SendMessage(message.Chat.Id, "💻 *Профиль разработчика*", parseMode: ParseMode.Markdown, replyMarkup: devBtn, cancellationToken: cancellationToken);
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
            await _botClient.SendMessage(message.Chat.Id, text, parseMode: ParseMode.Markdown, replyMarkup: builder, cancellationToken: cancellationToken);
        }

        private async Task HandleHelpAsync(Message message, CancellationToken cancellationToken)
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
            await _botClient.SendMessage(message.Chat.Id, helpText, parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
        }

        private async Task HandleLogsAsync(Message message, CancellationToken cancellationToken)
        {
            if (message.Chat.Type != ChatType.Private)
            {
                await _botClient.SendMessage(message.Chat.Id, "Доступно только в ЛС", cancellationToken: cancellationToken);
                return;
            }

            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "Нет доступа :/", cancellationToken: cancellationToken);
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
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var file in filesToZip)
                    {
                        var entry = archive.CreateEntry(file.Name);
                        using var entryStream = entry.Open();
                        using var fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fileStream.CopyTo(entryStream);
                    }
                }

                using var stream = File.OpenRead(zipPath);
                await _botClient.SendDocument(message.Chat.Id, InputFile.FromStream(stream, "logs.zip"), cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                await _botClient.SendMessage(message.Chat.Id, $"Ошибка при сборе логов: {TelegramEscaper.EscapeMarkdownV2(ex.Message)}", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
            }
            finally
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
            }
        }

        private async Task HandleSetWatermarkAsync(Message message, string messageText, CancellationToken cancellationToken)
        {
            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
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
                await _botClient.SendMessage(
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

            await _botClient.SendMessage(
                message.Chat.Id,
                $"✅ Текст водяного знака изменен на: `{TelegramEscaper.EscapeMarkdownV2(newWatermark)}`\\.\nКэш старых ватермарок в Telegram очищен\\.",
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);
        }

        private async Task HandleMakeAdminAsync(Message message, string messageText, CancellationToken cancellationToken)
        {
            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
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
                await _botClient.SendMessage(
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
                    ? "✅ Пользователь получил полный доступ к админ\\-командам\\."
                    : "ℹ️ Пользователь уже был администратором\\.") +
                "\n✦ ────────────── ✦";

            await _botClient.SendMessage(
                message.Chat.Id,
                adminText,
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);
        }

        private async Task HandleUnmakeAdminAsync(Message message, string messageText, CancellationToken cancellationToken)
        {
            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
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
                await _botClient.SendMessage(
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
                    ? "❌ Пользователь лишён админ\\-доступа\\."
                    : "ℹ️ Пользователь и так не был администратором\\.") +
                "\n✦ ────────────── ✦";

            await _botClient.SendMessage(
                message.Chat.Id,
                adminText,
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);
        }

        private async Task HandleSetPrefAsync(Message message, string messageText, CancellationToken cancellationToken)
        {
            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
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
                await _botClient.SendMessage(
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
                await _botClient.SendMessage(
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

            await _botClient.SendMessage(
                message.Chat.Id,
                prefixText,
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);
        }

        private async Task HandleProviderCommandAsync(Message message, string messageText, CancellationToken ct)
        {
            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
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
                await _botClient.SendMessage(
                    message.Chat.Id,
                    $"🔎 *Провайдер поиска*\\nАктивный: `{TelegramEscaper.EscapeMarkdownV2(_searchRegistry.ActiveKey)}`\\nДоступные: {TelegramEscaper.EscapeMarkdownV2(available)}\\n\\nИспользование: `/provider bing` или `/provider ddg`",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: ct);
                return;
            }

            if (_searchRegistry.SetActive(arg))
            {
                await _botClient.SendMessage(
                    message.Chat.Id,
                    $"✅ Провайдер переключён на `{TelegramEscaper.EscapeMarkdownV2(_searchRegistry.ActiveKey)}`",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: ct);
            }
            else
            {
                await _botClient.SendMessage(
                    message.Chat.Id,
                    $"⚠️ Неизвестный провайдер: `{TelegramEscaper.EscapeMarkdownV2(arg)}`",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: ct);
            }
        }

        private async Task HandleAdminsAsync(Message message, CancellationToken cancellationToken)
        {
            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                return;
            }

            var lines = new List<string>
            {
                "*Администрация*"
            };

            foreach (var adminId in _adminIds.OrderBy(id => id))
            {
                string? username = await TryGetUsernameAsync(adminId, cancellationToken);
                string display = string.IsNullOrEmpty(username) ? "без username" : $"@{username}";
                _prefs.Prefixes.TryGetValue(adminId, out string? pref);
                string prefix = string.IsNullOrEmpty(pref) ? "" : $"{TelegramEscaper.EscapeMarkdownV2(pref)} — ";
                lines.Add($"{prefix}{TelegramEscaper.EscapeMarkdownV2(display)} — `{adminId}`");
            }

            await _botClient.SendMessage(
                message.Chat.Id,
                string.Join("\n", lines),
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);
        }

        private async Task HandleRandomAsync(Message message, string messageText, CancellationToken cancellationToken)
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
                await _botClient.SendMessage(
                    message.Chat.Id,
                    "🎲 Укажите тему\\.\nПример: `/random кот`",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: cancellationToken);
                return;
            }

            await _broadcastService.SendRandomImageAsync(message.Chat.Id, topic, cancellationToken);
        }

        private async Task HandleBroadcastCommandAsync(Message message, string messageText, CancellationToken cancellationToken)
        {
            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
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
                await _botClient.SendMessage(
                    message.Chat.Id,
                    "⚠️ Укажите текст рассылки\\.\nПример: `/upt Важное обновление!`",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: cancellationToken);
                return;
            }

            await _broadcastService.SendBroadcastAsync(message.Chat.Id, announcement, cancellationToken);
        }

        private async Task<string?> TryGetUsernameAsync(long userId, CancellationToken cancellationToken)
        {
            try
            {
                var chat = await _botClient.GetChat(new ChatId(userId), cancellationToken: cancellationToken);
                return chat.Username;
            }
            catch
            {
                return null;
            }
        }
    }
}
