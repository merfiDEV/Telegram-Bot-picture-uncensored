using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using XzBotCs.Models;
using XzBotCs.Services;

namespace XzBotCs.Helpers
{
    /// <summary>
    /// UI-хелперы для Telegram: разметки, безопасные ответы на callback/inline,
    /// редактирование сообщений. Зависимости передаются параметрами.
    /// </summary>
    public static class TelegramUiHelper
    {
        public static InlineKeyboardMarkup BuildStatsMarkup(bool isWatermarkEnabled, string activeProviderKey)
        {
            string wmBtnText = isWatermarkEnabled ? "❌ Выключить ватермарку" : "✅ Включить ватермарку";
            string providerLabel = activeProviderKey == SearchProviderRegistry.DuckDuckGoKey ? "🔎 DDG" : "🔎 Bing";
            return new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("📈 Метрики", "stats:metrics"), InlineKeyboardButton.WithCallbackData("📋 Дашборд", "stats:dashboard") },
                new [] { InlineKeyboardButton.WithCallbackData(wmBtnText, "toggle_wm") },
                new [] { InlineKeyboardButton.WithCallbackData(providerLabel, "toggle_provider"), InlineKeyboardButton.WithCallbackData("🔄 Обновить", "stats:refresh") }
            });
        }

        public static InlineKeyboardMarkup BuildSourceMarkup(BingImageResult item)
        {
            string sourceUrl = item.SourceUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? item.SourceUrl
                : item.Url;
            string buttonText = item.SourceUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? "🌐 Перейти на сайт"
                : "🖼 Открыть оригинал";

            return new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl(buttonText, sourceUrl));
        }

        public static InlineQueryResultsButton BuildDeveloperInlineButton()
        {
            return new InlineQueryResultsButton("💻 Профиль разработчика >")
            {
                StartParameter = "developer"
            };
        }

        public static async Task<bool> TryAnswerInlineQueryAsync(
            ITelegramBotClient botClient,
            string inlineQueryId,
            IEnumerable<InlineQueryResult> results,
            int cacheTime,
            bool isPersonal,
            BotStatsService statsService,
            string nextOffset = "",
            CancellationToken cancellationToken = default)
        {
            try
            {
                await botClient.AnswerInlineQuery(
                    inlineQueryId,
                    results,
                    cacheTime: cacheTime,
                    isPersonal: isPersonal,
                    nextOffset: nextOffset,
                    button: BuildDeveloperInlineButton(),
                    cancellationToken: cancellationToken);
                return true;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && (
                ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("query expired", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase)))
            {
                statsService.RecordError("inline_timeout");
                Console.WriteLine($"Inline answer skipped: Telegram query expired ({ex.Message})");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error answering inline query: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> TryAnswerCallbackQueryAsync(
            ITelegramBotClient botClient,
            string callbackQueryId,
            string? text = null,
            bool showAlert = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await botClient.AnswerCallbackQuery(callbackQueryId, text, showAlert, cancellationToken: cancellationToken);
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

        public static async Task EditDashboardWithFallbackAsync(
            ITelegramBotClient botClient,
            Message message,
            string text,
            InlineKeyboardMarkup markup,
            CancellationToken ct)
        {
            try
            {
                if (message is { Type: MessageType.Photo })
                {
                    await botClient.EditMessageCaption(message.Chat.Id, message.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                }
                else
                {
                    await botClient.EditMessageText(message.Chat.Id, message.Id, text, parseMode: ParseMode.MarkdownV2, replyMarkup: markup, cancellationToken: ct);
                }
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("can't parse entities"))
            {
                Console.WriteLine($"Dashboard markdown parse failed, fallback to plain: {ex.Message}");
                Console.WriteLine($"Text was: {text}");
                try
                {
                    if (message is { Type: MessageType.Photo })
                        await botClient.EditMessageCaption(message.Chat.Id, message.Id, text, replyMarkup: markup, cancellationToken: ct);
                    else
                        await botClient.EditMessageText(message.Chat.Id, message.Id, text, replyMarkup: markup, cancellationToken: ct);
                }
                catch (Exception ex2)
                {
                    Console.WriteLine($"Fallback also failed: {ex2.Message}");
                }
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified")) { }
        }

        public static async Task AnswerRegistrationRequiredAsync(
            ITelegramBotClient botClient,
            string inlineQueryId,
            string? botUsername,
            BotStatsService statsService,
            CancellationToken cancellationToken)
        {
            string? botUrl = string.IsNullOrEmpty(botUsername)
                ? null
                : $"https://t.me/{botUsername}?start=register";

            var result = new InlineQueryResultArticle(
                "register-required",
                "🔒 Подтвердите регистрацию",
                new InputTextMessageContent("Для использования бота откройте личные сообщения и подтвердите регистрацию."))
            {
                Description = "Нажмите, чтобы перейти в ЛС с ботом",
                ReplyMarkup = botUrl == null
                    ? null
                    : new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("✅ Перейти в ЛС", botUrl))
            };

            await TryAnswerInlineQueryAsync(
                botClient,
                inlineQueryId,
                new[] { result },
                cacheTime: 0,
                isPersonal: true,
                statsService: statsService,
                cancellationToken: cancellationToken);
        }

        public static async Task AnswerEmptyInlineQueryAsync(
            ITelegramBotClient botClient,
            string inlineQueryId,
            BotStatsService statsService,
            CancellationToken cancellationToken)
        {
            var emptyResult = new InlineQueryResultArticle(
                "empty-query",
                "🔍 Введите запрос",
                new InputTextMessageContent("Введите запрос после имени бота, и я найду картинки."))
            {
                Description = "Напишите, какую картинку найти. Например: кот в очках"
            };

            await TryAnswerInlineQueryAsync(
                botClient,
                inlineQueryId,
                new[] { emptyResult },
                cacheTime: 0,
                isPersonal: true,
                statsService: statsService,
                cancellationToken: cancellationToken);
        }
    }
}
