using System.Threading.Tasks;
using XzBotCs.Services;

namespace XzBotCs.Interfaces
{
    public interface ISearchService
    {
        Task<BingSearchResponse> SearchImagesDetailedAsync(string query, int startIndex = 1, int limit = 30);
        Task<(bool Ok, string Status)> CheckBingAsync();
    }
}
