using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using XzBotCs.Helpers;
using XzBotCs.Models;
using XzBotCs.Services;

namespace XzBotCs.Handlers
{
    /// <summary>
    /// Обработка callback-запросов (нажатия inline-кнопок): статистика, дашборд,
    /// переключение ватермарки и провайдера.
    /// </summary>
    public class CallbackQueryHandler
    {
        public const string DashPage = "dash:page:";
        public const string DashFilter = "dash:filter:";
        public const string DashSort = "dash:sort:";
        public const string DashSearch = "dash:search";
        public const string DashRefresh = "dash:refresh";
        public const string DashClearSearch = "dash:clear_search";
        public const string DashNoop = "dash:noop";

        private readonly ITelegramBotClient _botClient;
        private readonly BotState _state;
        private readonly SearchProviderRegistry _searchRegistry;
        private readonly BotStatsService _statsService;
        private readonly StatsMessageService _statsMessageService;
        private readonly HashSet<long> _adminIds;

        public CallbackQueryHandler(
            ITelegramBotClient botClient,
            BotState state,
            SearchProviderRegistry searchRegistry,
            BotStatsService statsService,
            StatsMessageService statsMessageService,
            HashSet<long> adminIds)
        {
            _botClient = botClient;
            _state = state;
            _searchRegistry = searchRegistry;
            _statsService = statsService;
            _statsMessageService = statsMessageService;
            _adminIds = adminIds;
        }

        public async Task HandleAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            if (_adminIds.Count == 0 || !_adminIds.Contains(callbackQuery.From.Id))
            {
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, "⛔ Нет доступа", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            if (callbackQuery.Data == "toggle_wm")
            {
                _state.IsWatermarkEnabled = !_state.IsWatermarkEnabled;
                _state.Save();
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, $"Ватермарка: {(_state.IsWatermarkEnabled ? "ВКЛ" : "ВЫКЛ")}", cancellationToken: cancellationToken);
                await _statsMessageService.RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
            }
            else if (callbackQuery.Data == "toggle_provider")
            {
                string? next = _searchRegistry.FallbackKey;
                if (next != null && _searchRegistry.SetActive(next))
                {
                    await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, $"Провайдер: {_searchRegistry.ActiveKey.ToUpperInvariant()}", cancellationToken: cancellationToken);
                }
                else
                {
                    await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, "Не удалось переключить", showAlert: true, cancellationToken: cancellationToken);
                }
                await _statsMessageService.RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
            }
            else if (callbackQuery.Data == "stats:refresh")
            {
                await _statsMessageService.RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, "Обновлено ✅", cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data == "stats:back")
            {
                await _statsMessageService.RefreshStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, cancellationToken);
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, cancellationToken: cancellationToken);
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
                        await _botClient.EditMessageCaption(photoMsg.Chat.Id, photoMsg.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: cancellationToken);
                    }
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified")) { }

                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data == "stats:dashboard")
            {
                await ShowDashboardAsync(callbackQuery, _statsService.BuildDashboardText(callbackQuery.From.Id), cancellationToken);
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data != null && callbackQuery.Data.StartsWith(DashPage))
            {
                string pageStr = callbackQuery.Data.Substring(DashPage.Length);
                if (int.TryParse(pageStr, out int newPage))
                {
                    await ShowDashboardAsync(callbackQuery, _statsService.BuildDashboardText(callbackQuery.From.Id, page: newPage), cancellationToken);
                }
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data != null && callbackQuery.Data.StartsWith(DashFilter))
            {
                string filter = callbackQuery.Data.Substring(DashFilter.Length);
                await ShowDashboardAsync(callbackQuery, _statsService.BuildDashboardText(callbackQuery.From.Id, page: 0, filter: filter), cancellationToken);
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data == DashRefresh)
            {
                await ShowDashboardAsync(callbackQuery, _statsService.BuildDashboardText(callbackQuery.From.Id), cancellationToken);
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, "Обновлено ✅", cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data == "dash:search:clear")
            {
                string text = _statsService.BuildDashboardText(callbackQuery.From.Id, page: 0, search: string.Empty);
                if (_state.DashboardStates.TryGetValue(callbackQuery.From.Id, out var ds)) ds.Search = string.Empty;
                await ShowDashboardAsync(callbackQuery, text, cancellationToken);
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, "Поиск сброшен", cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data == DashSearch)
            {
                _statsService.SetDashboardAwaitingSearch(callbackQuery.From.Id, true);
                await TelegramUiHelper.TryAnswerCallbackQueryAsync(_botClient, callbackQuery.Id, "Отправьте текст для поиска в ЛС бота", showAlert: true, cancellationToken: cancellationToken);
            }
        }

        private async Task ShowDashboardAsync(CallbackQuery callbackQuery, string text, CancellationToken ct)
        {
            var markup = _statsService.BuildDashboardMarkup(callbackQuery.From.Id);
            var dashButtons = markup.InlineKeyboard.ToList();
            dashButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "stats:back") });
            markup = new InlineKeyboardMarkup(dashButtons);
            if (callbackQuery.Message != null)
                await TelegramUiHelper.EditDashboardWithFallbackAsync(_botClient, callbackQuery.Message, text, markup, ct);
        }
    }
}
