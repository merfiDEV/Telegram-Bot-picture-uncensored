using System;

namespace XzBotCs.Interfaces
{
    public interface IStatsService
    {
        void IncrementUsage();
        void RecordResponseTime(TimeSpan elapsed);
        void RecordResponseTime(string providerKey, TimeSpan elapsed);
        void RecordError(string errorType);
        void RecordRequest(long userId, string? username, string query, bool success);
        string BuildStatsText(IEnumerable<(string Key, string DisplayName, bool Ok, string Status)> providerStatuses);
        string BuildMetricsText();
        string BuildDashboardText();
        byte[] GenerateChartImage();
    }
}
