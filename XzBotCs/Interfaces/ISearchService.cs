using System.Threading.Tasks;
using XzBotCs.Services;

namespace XzBotCs.Interfaces
{
    public interface ISearchService
    {
        Task<BingSearchResponse> SearchImagesDetailedAsync(string query, int startIndex = 1, int limit = 30);
        Task<(bool Ok, string Status)> CheckBingAsync();

        /// <summary>Ключ провайдера, который обслужил последний поиск ("bing"/"ddg").</summary>
        string ActiveProviderKey { get; }
    }

    /// <summary>
    /// Провайдер поиска изображений. Каждый провайдер (Bing, DuckDuckGo, ...)
    /// реализует этот контракт и регистрируется в <see cref="SearchProviderRegistry"/>.
    /// </summary>
    public interface IImageSearchProvider
    {
        /// <summary>Стабильный ключ провайдера, например "bing" или "ddg".</summary>
        string Key { get; }

        /// <summary>Отображаемое имя для статистики и настроек.</summary>
        string DisplayName { get; }

        Task<BingSearchResponse> SearchImagesDetailedAsync(string query, int startIndex = 1, int limit = 30);

        Task<(bool Ok, string Status)> CheckAsync();
    }
}