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
        private static Handlers.MessageCommandHandler _messageCommandHandler = null!;
        private static Handlers.InlineQueryService _inlineQueryService = null!;
        private static Proxy.ProxyServer _proxyServer = null!;
        private static PrefStore _prefs = PrefStore.Load();
        private static HttpClient _httpClient = new HttpClient();
        private static readonly HashSet<long> _adminIds = new HashSet<long>();
        private static long? _cacheChatId;
        private static string? _proxyBaseUrl;
        private static string? _botUsername;
        private static readonly SemaphoreSlim _watermarkUploadLock = new SemaphoreSlim(3);
        private static readonly TimeSpan FlagRegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly object _stateLock = new object();

        private const int DefaultProxyPort = 8080;
        private const string DefaultProxyBaseUrl = "http://46.229.63.243:8080/img?u=";

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
            _messageCommandHandler = new Handlers.MessageCommandHandler(_botClient, _state, _prefs, _adminIds, _statsService, _broadcastService, _statsMessageService, _searchRegistry);

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

            _inlineQueryService = new Handlers.InlineQueryService(
                _botClient, _state, _adminIds, _statsService, _searchService, _searchRegistry,
                _watermarkService, _httpClient, _watermarkUploadLock, FlagRegexTimeout,
                botUsername: null, cacheChatId: _cacheChatId, proxyBaseUrl: _proxyBaseUrl);
            _proxyServer = new Proxy.ProxyServer(_httpClient, _watermarkService, _state, _inlineQueryService);

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token
            );

            _ = Task.Run(() => _proxyServer.StartAsync(proxyPort, cts.Token));

            var me = await _botClient.GetMe(cts.Token);
            _botUsername = me.Username;
            _inlineQueryService.BotUsername = me.Username;
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
                    await _messageCommandHandler.HandleAsync(message, messageText, cancellationToken);
                }
                else if (update.CallbackQuery is { } callbackQuery)
                {
                    await _callbackQueryHandler.HandleAsync(callbackQuery, cancellationToken);
                }
                else if (update.InlineQuery is { } inlineQuery)
                {
                    await _inlineQueryService.HandleAsync(inlineQuery, cancellationToken);
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



    }
}
