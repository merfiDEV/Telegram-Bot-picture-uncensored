namespace XzBotCs.Interfaces
{
    public interface IWatermarkService
    {
        Services.WatermarkResult ApplyWatermarkOrOriginal(byte[] imageBytes, string originalContentType, string watermarkText);
    }
}
