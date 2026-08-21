using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using XzBotCs.Interfaces;
using XzBotCs.Models;

namespace XzBotCs.Handlers
{
    public class UpdateHandler
    {
        private readonly ITelegramBotClient _botClient;
        private readonly CommandHandler _commandHandler;
        private readonly InlineQueryHandler _inlineQueryHandler;
        private readonly ISearchService _searchService;
        private readonly IStatsService _statsService;
        private readonly BotState _state;
        private readonly HashSet<long> _adminIds;
        private readonly string? _proxyBaseUrl;
        private readonly Func<bool> _getProxyListenerStarted;

        public UpdateHandler(
            ITelegramBotClient botClient,
            CommandHandler commandHandler,
            InlineQueryHandler inlineQueryHandler,
            ISearchService searchService,
            IStatsService statsService,
            BotState state,
            HashSet<long> adminIds,
            string? proxyBaseUrl,
            Func<bool> getProxyListenerStarted)
        {
            _botClient = botClient;
            _commandHandler = commandHandler;
            _inlineQueryHandler = inlineQueryHandler;
            _searchService = searchService;
            _statsService = statsService;
            _state = state;
            _adminIds = adminIds;
            _proxyBaseUrl = proxyBaseUrl;
            _getProxyListenerStarted = getProxyListenerStarted;
        }

        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Message is { } message && message.Text is { } messageText)
                {
                    if (messageText.StartsWith("/start"))
                    {
                        await _commandHandler.HandleStartAsync(message, messageText, cancellationToken);
                    }
                    else if (messageText == "/stats")
                    {
                        await _commandHandler.HandleStatsAsync(message, cancellationToken);
                    }
                    else if (messageText == "/logs")
                    {
                        await _commandHandler.HandleLogsAsync(message, cancellationToken);
                    }
                    else if (messageText.StartsWith("/setwatermark"))
                    {
                        await _commandHandler.HandleSetWatermarkAsync(message, messageText, cancellationToken);
                    }
                }
                else if (update.CallbackQuery is { } callbackQuery)
                {
                    await HandleCallbackQueryAsync(callbackQuery, cancellationToken);
                }
                else if (update.InlineQuery is { } inlineQuery)
                {
                    await _inlineQueryHandler.HandleInlineQueryAsync(inlineQuery, _proxyBaseUrl ?? "", _getProxyListenerStarted(), cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update handler error: {ex}");
            }
        }

        public Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine(exception);
            return Task.CompletedTask;
        }

        private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken ct)
        {
            if (!_commandHandler.IsAdmin(callbackQuery.From.Id))
            {
                await TryAnswerCallbackQueryAsync(callbackQuery.Id, "⛔ Нет доступа", showAlert: true, cancellationToken: ct);
                return;
            }

            if (callbackQuery.Data == "toggle_wm")
            {
                _state.IsWatermarkEnabled = !_state.IsWatermarkEnabled;
                _state.Save();
                await TryAnswerCallbackQueryAsync(callbackQuery.Id, $"Ватермарка: {(_state.IsWatermarkEnabled ? "ВКЛ" : "ВЫКЛ")}", cancellationToken: ct);
                await RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, ct);
            }
            else if (callbackQuery.Data == "stats:refresh")
            {
                await RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, ct);
                await TryAnswerCallbackQueryAsync(callbackQuery.Id, "Обновлено ✅", cancellationToken: ct);
            }
            else if (callbackQuery.Data == "stats:back")
            {
                await RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, ct);
                await TryAnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            }
            else if (callbackQuery.Data == "stats:metrics")
            {
                string text = _statsService.BuildMetricsText();
                var markup = new InlineKeyboardMarkup(new[] {
                    new [] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "stats:back") },
                    new [] { InlineKeyboardButton.WithCallbackData("🔄 Обновить", "stats:metrics") }
                });
                try
                {
                    if (callbackQuery.Message is Message { Type: MessageType.Photo } photoMsg)
                    {
                        await _botClient.EditMessageCaption(photoMsg.Chat.Id, photoMsg.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                    }
                    else
                    {
                        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                    }
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified")) { }

                await TryAnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            }
            else if (callbackQuery.Data == "stats:dashboard")
            {
                string text = _statsService.BuildDashboardText();
                var markup = new InlineKeyboardMarkup(new[] {
                    new [] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "stats:back") },
                    new [] { InlineKeyboardButton.WithCallbackData("🔄 Обновить", "stats:dashboard") }
                });
                try
                {
                    if (callbackQuery.Message is Message { Type: MessageType.Photo } photoMsg)
                    {
                        await _botClient.EditMessageCaption(photoMsg.Chat.Id, photoMsg.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                    }
                    else
                    {
                        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                    }
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified")) { }

                await TryAnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            }
        }

        private async Task RefreshStatsAsync(long chatId, int messageId, CancellationToken ct)
        {
            var (bingOk, bingStatus) = await _searchService.CheckBingAsync();
            var text = _statsService.BuildStatsText(bingOk, bingStatus);
            var markup = _commandHandler.BuildStatsMarkup();
            try
            {
                var chartBytes = _statsService.GenerateChartImage();
                if (chartBytes.Length > 0)
                {
                    using var ms = new System.IO.MemoryStream(chartBytes);
                    await _botClient.EditMessageMedia(chatId, messageId, new InputMediaPhoto(InputFile.FromStream(ms, "stats.png")), cancellationToken: ct);
                    await _botClient.EditMessageCaption(chatId, messageId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                }
                else
                {
                    try
                    {
                        await _botClient.EditMessageText(chatId, messageId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                    }
                    catch (ApiRequestException ex) when (ex.Message.Contains("there is no text in the message to edit"))
                    {
                        await _botClient.EditMessageCaption(chatId, messageId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                    }
                }
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified")) { }
            catch (Exception ex)
            {
                Console.WriteLine($"RefreshStats error: {ex.Message}");
            }
        }

        private async Task<bool> TryAnswerCallbackQueryAsync(
            string callbackQueryId,
            string? text = null,
            bool showAlert = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _botClient.AnswerCallbackQuery(callbackQueryId, text, showAlert, cancellationToken: cancellationToken);
                return true;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && (
                ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("query expired", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"Callback answer skipped: Telegram query expired ({ex.Message})");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error answering callback query: {ex.Message}");
                return false;
            }
        }
    }
}
