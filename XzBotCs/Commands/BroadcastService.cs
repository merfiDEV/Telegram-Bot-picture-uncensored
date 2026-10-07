using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using XzBotCs.Helpers;
using XzBotCs.Interfaces;
using XzBotCs.Models;
using XzBotCs.Services;

namespace XzBotCs.Commands
{
    /// <summary>
    /// Долгие команды: рассылка подписчикам и отправка случайной картинки.
    /// </summary>
    public class BroadcastService
    {
        private readonly ITelegramBotClient _botClient;
        private readonly BotState _state;
        private readonly ISearchService _searchService;
        private readonly BotStatsService _statsService;
        private readonly HttpClient _httpClient;

        public BroadcastService(
            ITelegramBotClient botClient,
            BotState state,
            ISearchService searchService,
            BotStatsService statsService,
            HttpClient httpClient)
        {
            _botClient = botClient;
            _state = state;
            _searchService = searchService;
            _statsService = statsService;
            _httpClient = httpClient;
        }

        public async Task SendRandomImageAsync(long chatId, string topic, CancellationToken ct)
        {
            await _botClient.SendChatAction(chatId, ChatAction.UploadPhoto, cancellationToken: ct);

            int limit = _state.IsWatermarkEnabled ? 6 : 30;
            var response = await _searchService.SearchImagesDetailedAsync(topic, startIndex: 1, limit: limit);
            var items = response.Items;
            if (items.Count == 0)
            {
                await _botClient.SendMessage(
                    chatId,
                    "😕 Ничего не нашлось по запросу\\. Попробуй другую тему\\.",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: ct);
                return;
            }

            _statsService.RecordRequest(0, null, topic + " --random", true);

            // Перемешиваем список элементов, чтобы совершать попытки отправки по случайному порядку
            var shuffledItems = items.OrderBy(_ => Random.Shared.Next()).ToList();
            bool sent = false;

            foreach (var pick in shuffledItems)
            {
                var markup = TelegramUiHelper.BuildSourceMarkup(pick);
                var caption = $"🎲 *{TelegramEscaper.EscapeMarkdownV2(topic)}*";

                try
                {
                    if (pick.IsGif)
                    {
                        await _botClient.SendAnimation(
                            chatId,
                            InputFile.FromUri(pick.Url),
                            caption: caption,
                            parseMode: ParseMode.MarkdownV2,
                            replyMarkup: markup,
                            cancellationToken: ct);
                    }
                    else
                    {
                        await _botClient.SendPhoto(
                            chatId,
                            InputFile.FromUri(pick.Url),
                            caption: caption,
                            parseMode: ParseMode.MarkdownV2,
                            replyMarkup: markup,
                            cancellationToken: ct);
                    }
                    sent = true;
                    break;
                }
                catch (ApiRequestException ex) when (ex.Message.Contains("failed to get HTTP URL content") || ex.ErrorCode == 400)
                {
                    // Ошибка получения контента по URL со стороны Telegram. Пробуем скачать локально и отправить потоком.
                    try
                    {
                        using var httpResp = await _httpClient.GetAsync(pick.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (httpResp.IsSuccessStatusCode)
                        {
                            await using var stream = await httpResp.Content.ReadAsStreamAsync(ct);
                            var filename = pick.IsGif ? "animation.gif" : "photo.jpg";
                            var inputFile = InputFile.FromStream(stream, filename);

                            if (pick.IsGif)
                            {
                                await _botClient.SendAnimation(
                                    chatId,
                                    inputFile,
                                    caption: caption,
                                    parseMode: ParseMode.MarkdownV2,
                                    replyMarkup: markup,
                                    cancellationToken: ct);
                            }
                            else
                            {
                                await _botClient.SendPhoto(
                                    chatId,
                                    inputFile,
                                    caption: caption,
                                    parseMode: ParseMode.MarkdownV2,
                                    replyMarkup: markup,
                                    cancellationToken: ct);
                            }
                            sent = true;
                            break;
                        }
                    }
                    catch
                    {
                        // Пропускаем проблемную ссылку и пробуем следующую картинку
                    }
                }
                catch
                {
                    // При иных ошибках отправки пробуем следующий найденный объект
                }
            }

            if (!sent)
            {
                await _botClient.SendMessage(
                    chatId,
                    "😕 Не удалось загрузить ни одно изображение по вашему запросу\\. Попробуйте другую тему\\.",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: ct);
            }
        }

        public async Task SendBroadcastAsync(long adminChatId, string announcement, CancellationToken ct)
        {
            List<long> subscribers;
            lock (_state.SyncRoot)
            {
                subscribers = _state.Subscribers.ToList();
            }
            if (subscribers.Count == 0)
            {
                await _botClient.SendMessage(adminChatId, "📭 Список подписчиков пуст.", cancellationToken: ct);
                return;
            }

            await _botClient.SendMessage(
                adminChatId,
                $"🚀 Начинаю рассылку для `{subscribers.Count}` подписчиков\\.\\.\\.",
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: ct);

            int success = 0;
            int failed = 0;
            var failedIds = new List<long>();

            foreach (var userId in subscribers)
            {
                try
                {
                    await _botClient.SendMessage(
                        userId,
                        "📢 *Обновление*\n\n" + announcement,
                        parseMode: ParseMode.Markdown,
                        cancellationToken: ct);
                    success++;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 403)
                {
                    failed++;
                    lock (_state.SyncRoot)
                    {
                        _state.Subscribers.Remove(userId);
                    }
                    failedIds.Add(userId);
                    Console.WriteLine($"Broadcast: user {userId} blocked the bot, removed from subscribers.");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"Broadcast: failed to send to {userId}: {ex.Message}");
                }

                await Task.Delay(50, ct);
            }

            if (failedIds.Count > 0)
            {
                _state.Save();
            }

            await _botClient.SendMessage(
                adminChatId,
                $"✅ Рассылка завершена\\.\n\n" +
                $"📨 Отправлено: `{success}`\n" +
                $"❌ Не удалось: `{failed}`",
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: ct);
        }
    }
}
