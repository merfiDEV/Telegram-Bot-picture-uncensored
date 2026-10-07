using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using XzBotCs.Helpers;
using XzBotCs.Models;

namespace XzBotCs.Services
{
    /// <summary>
    /// Отправка и обновление сообщения со статистикой (текст + график).
    /// </summary>
    public class StatsMessageService
    {
        private readonly ITelegramBotClient _botClient;
        private readonly SearchProviderRegistry _searchRegistry;
        private readonly BotStatsService _statsService;
        private readonly BotState _state;

        public StatsMessageService(
            ITelegramBotClient botClient,
            SearchProviderRegistry searchRegistry,
            BotStatsService statsService,
            BotState state)
        {
            _botClient = botClient;
            _searchRegistry = searchRegistry;
            _statsService = statsService;
            _state = state;
        }

        private InlineKeyboardMarkup BuildMarkup()
            => TelegramUiHelper.BuildStatsMarkup(_state.IsWatermarkEnabled, _searchRegistry.ActiveKey);

        public async Task SendStatsAsync(long chatId, CancellationToken ct)
        {
            var providerStatuses = await _searchRegistry.CheckAllAsync();
            var text = _statsService.BuildStatsText(providerStatuses);
            var markup = BuildMarkup();

            var chartBytes = _statsService.GenerateChartImage();
            if (chartBytes.Length > 0)
            {
                using var ms = new MemoryStream(chartBytes);
                await _botClient.SendPhoto(chatId, InputFile.FromStream(ms, "stats.png"), caption: text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
            }
            else
            {
                await _botClient.SendMessage(chatId, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
            }
        }

        public async Task RefreshStatsAsync(long chatId, int messageId, CancellationToken ct)
        {
            var providerStatuses = await _searchRegistry.CheckAllAsync();
            var text = _statsService.BuildStatsText(providerStatuses);
            var markup = BuildMarkup();
            try
            {
                var chartBytes = _statsService.GenerateChartImage();
                if (chartBytes.Length > 0)
                {
                    using var ms = new MemoryStream(chartBytes);
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
            catch { }
        }
    }
}
