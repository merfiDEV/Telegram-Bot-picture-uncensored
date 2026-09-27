using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XzBotCs.Interfaces;
using XzBotCs.Models;

namespace XzBotCs.Services
{
    /// <summary>
    /// Реестр провайдеров поиска изображений. Держит список доступных провайдеров
    /// и активный выбор пользователя (в <see cref="BotState.SearchProvider"/>).
    /// </summary>
    public class SearchProviderRegistry
    {
        public const string BingKey = "bing";
        public const string DuckDuckGoKey = "ddg";

        private readonly Dictionary<string, IImageSearchProvider> _providers =
            new Dictionary<string, IImageSearchProvider>(StringComparer.OrdinalIgnoreCase);

        private readonly object _lock = new object();
        private readonly BotState _state;

        public SearchProviderRegistry(BotState state)
        {
            _state = state;

            Register(new BingSearchProvider(new BingSearchService()));
            Register(new DuckDuckGoSearchProvider(new DuckDuckGoSearchService()));
        }

        /// <summary>Ключ провайдера, который используется для автоматического фолбэка (не совпадает с активным).</summary>
        public string? FallbackKey
        {
            get
            {
                string active = ActiveKey;
                lock (_lock)
                {
                    foreach (var key in _providers.Keys)
                    {
                        if (!string.Equals(key, active, StringComparison.OrdinalIgnoreCase))
                        {
                            return key;
                        }
                    }
                }

                return null;
            }
        }

        public void Register(IImageSearchProvider provider)
        {
            lock (_lock)
            {
                _providers[provider.Key] = provider;
            }
        }

        public IReadOnlyList<IImageSearchProvider> All
        {
            get
            {
                lock (_lock)
                {
                    return _providers.Values
                        .OrderBy(p => p.Key == BingKey ? 0 : 1)
                        .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
        }

        /// <summary>Проверяет доступность всех провайдеров параллельно. Возвращает (Key, DisplayName, Ok, Status) для каждого.</summary>
        public async Task<List<(string Key, string DisplayName, bool Ok, string Status)>> CheckAllAsync()
        {
            var providers = All;
            var tasks = providers.Select(async p =>
            {
                try
                {
                    var (ok, status) = await p.CheckAsync();
                    return (p.Key, p.DisplayName, ok, status);
                }
                catch (Exception ex)
                {
                    return (p.Key, p.DisplayName, false, ex.GetType().Name);
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        /// <summary>Ключ активного провайдера; при некорректном значении откатывается на Bing.</summary>
        public string ActiveKey
        {
            get
            {
                string key = _state.SearchProvider;
                if (string.IsNullOrWhiteSpace(key) || !_providers.ContainsKey(key))
                {
                    return BingKey;
                }

                return _providers[key].Key;
            }
        }

        public IImageSearchProvider Active => Get(ActiveKey) ?? _providers[BingKey];

        public IImageSearchProvider? Get(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            lock (_lock)
            {
                return _providers.TryGetValue(key, out var provider) ? provider : null;
            }
        }

        public bool SetActive(string key)
        {
            var provider = Get(key);
            if (provider == null) return false;

            lock (_state.SyncRoot)
            {
                _state.SearchProvider = provider.Key;
            }

            _state.Save();
            return true;
        }
    }

    /// <summary>Обёртка Bing как провайдера.</summary>
    public class BingSearchProvider : IImageSearchProvider
    {
        private readonly BingSearchService _service;

        public BingSearchProvider(BingSearchService service)
        {
            _service = service;
        }

        public string Key => SearchProviderRegistry.BingKey;
        public string DisplayName => "Bing";

        public Task<BingSearchResponse> SearchImagesDetailedAsync(string query, int startIndex = 1, int limit = 30)
            => _service.SearchImagesDetailedAsync(query, startIndex, limit);

        public Task<(bool Ok, string Status)> CheckAsync() => _service.CheckBingAsync();
    }

    /// <summary>Обёртка DuckDuckGo как провайдера.</summary>
    public class DuckDuckGoSearchProvider : IImageSearchProvider
    {
        private readonly DuckDuckGoSearchService _service;

        public DuckDuckGoSearchProvider(DuckDuckGoSearchService service)
        {
            _service = service;
        }

        public string Key => SearchProviderRegistry.DuckDuckGoKey;
        public string DisplayName => "DuckDuckGo";

        public Task<BingSearchResponse> SearchImagesDetailedAsync(string query, int startIndex = 1, int limit = 30)
            => _service.SearchImagesDetailedAsync(query, startIndex, limit);

        public Task<(bool Ok, string Status)> CheckAsync() => _service.CheckDuckDuckGoAsync();
    }

    /// <summary>
    /// ISearchService, который делегирует вызовы активному провайдеру из реестра.
    /// Благодаря этому все существующие хендлеры автоматически поддерживают
    /// переключение провайдера без изменения своего кода.
    /// </summary>
    public class RoutingSearchService : ISearchService
    {
        private readonly SearchProviderRegistry _registry;

        public RoutingSearchService(SearchProviderRegistry registry)
        {
            _registry = registry;
        }

        public SearchProviderRegistry Registry => _registry;

        public string ActiveProviderKey => _registry.ActiveKey;

        public async Task<BingSearchResponse> SearchImagesDetailedAsync(string query, int startIndex = 1, int limit = 30)
        {
            var primary = _registry.Active;
            var response = await primary.SearchImagesDetailedAsync(query, startIndex, limit);

            // Автофолбэк: если активный провайдер не нашёл ничего (или упал), пробуем второй.
            bool empty = response.Items.Count == 0;
            if (empty)
            {
                string? fallbackKey = _registry.FallbackKey;
                var fallback = _registry.Get(fallbackKey);
                if (fallback != null && !string.Equals(fallback.Key, primary.Key, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Provider '{primary.Key}' returned 0 results, falling back to '{fallback.Key}' for '{query}'");
                    var fallbackResponse = await fallback.SearchImagesDetailedAsync(query, startIndex, limit);
                    if (fallbackResponse.Items.Count > 0)
                    {
                        return fallbackResponse;
                    }
                }
            }

            return response;
        }

        public Task<(bool Ok, string Status)> CheckBingAsync()
            => _registry.Active.CheckAsync();
    }
}