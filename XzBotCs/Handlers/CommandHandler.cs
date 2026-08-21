using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using XzBotCs.Helpers;
using XzBotCs.Interfaces;
using XzBotCs.Models;

namespace XzBotCs.Handlers
{
    public class CommandHandler
    {
        private readonly ITelegramBotClient _botClient;
        private readonly IStatsService _statsService;
        private readonly ISearchService _searchService;
        private readonly BotState _state;
        private readonly HashSet<long> _adminIds;

        public CommandHandler(
            ITelegramBotClient botClient,
            IStatsService statsService,
            ISearchService searchService,
            BotState state,
            HashSet<long> adminIds)
        {
            _botClient = botClient;
            _statsService = statsService;
            _searchService = searchService;
            _state = state;
            _adminIds = adminIds;
        }

        public async Task HandleStartAsync(Message message, string messageText, CancellationToken ct)
        {
            if (messageText.Contains("developer"))
            {
                var devBtn = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("💻 Открыть профиль", AppConfig.DeveloperProfileUrl));
                await _botClient.SendMessage(message.Chat.Id, "💻 *Профиль разработчика*", parseMode: ParseMode.Markdown, replyMarkup: devBtn, cancellationToken: ct);
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
            await _botClient.SendMessage(message.Chat.Id, text, parseMode: ParseMode.Markdown, replyMarkup: builder, cancellationToken: ct);
        }

        public async Task HandleStatsAsync(Message message, CancellationToken ct)
        {
            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                return;
            }

            var (bingOk, bingStatus) = await _searchService.CheckBingAsync();
            var text = _statsService.BuildStatsText(bingOk, bingStatus);
            var markup = BuildStatsMarkup();

            var chartBytes = _statsService.GenerateChartImage();
            if (chartBytes.Length > 0)
            {
                using var ms = new MemoryStream(chartBytes);
                await _botClient.SendPhoto(message.Chat.Id, InputFile.FromStream(ms, "stats.png"), caption: text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
            }
            else
            {
                await _botClient.SendMessage(message.Chat.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
            }
        }

        public async Task HandleLogsAsync(Message message, CancellationToken ct)
        {
            if (message.Chat.Type != ChatType.Private)
            {
                await _botClient.SendMessage(message.Chat.Id, "Доступно только в ЛС", cancellationToken: ct);
                return;
            }

            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "Нет доступа :/", cancellationToken: ct);
                return;
            }

            string logsPath = "../logs";
            if (!Directory.Exists(logsPath)) Directory.CreateDirectory(logsPath);

            string zipPath = Path.Combine(Path.GetTempPath(), $"logs_{Guid.NewGuid():N}.zip");
            try
            {
                using (var fs = new FileStream(zipPath, FileMode.Create))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var file in Directory.GetFiles(logsPath))
                    {
                        var entry = archive.CreateEntry(Path.GetFileName(file));
                        using var entryStream = entry.Open();
                        using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fileStream.CopyTo(entryStream);
                    }
                }

                using var stream = File.OpenRead(zipPath);
                await _botClient.SendDocument(message.Chat.Id, InputFile.FromStream(stream, "logs.zip"), cancellationToken: ct);
            }
            catch (Exception ex)
            {
                await _botClient.SendMessage(message.Chat.Id, $"Ошибка при сборе логов: {TelegramEscaper.EscapeMarkdownV2(ex.Message)}", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
            }
            finally
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
            }
        }

        public async Task HandleSetWatermarkAsync(Message message, string messageText, CancellationToken ct)
        {
            if (!IsAdmin(message.From?.Id))
            {
                await _botClient.SendMessage(message.Chat.Id, "⛔ Нет доступа", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
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
                    cancellationToken: ct);
                return;
            }

            _state.WatermarkText = newWatermark;
            _state.WatermarkFileIds.Clear();
            _state.Save();

            await _botClient.SendMessage(
                message.Chat.Id,
                $"✅ Текст водяного знака изменен на: `{TelegramEscaper.EscapeMarkdownV2(newWatermark)}`\\.\nКэш старых ватермарок в Telegram очищен\\.",
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: ct);
        }

        public InlineKeyboardMarkup BuildStatsMarkup()
        {
            string wmBtnText = _state.IsWatermarkEnabled ? "❌ Выключить ватермарку" : "✅ Включить ватермарку";
            return new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("📈 Метрики", "stats:metrics"), InlineKeyboardButton.WithCallbackData("📋 Дашборд", "stats:dashboard") },
                new [] { InlineKeyboardButton.WithCallbackData(wmBtnText, "toggle_wm") },
                new [] { InlineKeyboardButton.WithCallbackData("🔄 Обновить", "stats:refresh") }
            });
        }

        public bool IsAdmin(long? userId)
        {
            return userId.HasValue && (_adminIds.Count == 0 || _adminIds.Contains(userId.Value));
        }
    }
}
