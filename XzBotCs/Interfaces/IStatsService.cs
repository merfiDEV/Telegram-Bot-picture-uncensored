using System;

namespace XzBotCs.Interfaces
{
    public interface IStatsService
    {
        void IncrementUsage();
        void RecordResponseTime(TimeSpan elapsed);
        void RecordError(string errorType);
        void RecordRequest(long userId, string? username, string query, bool success);
        string BuildStatsText(bool bingOk, string bingStatus);
        string BuildMetricsText();
        string BuildDashboardText();
        byte[] GenerateChartImage();
    }
}
