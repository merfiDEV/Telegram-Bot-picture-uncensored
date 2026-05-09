using SkiaSharp;
using System.IO;

namespace XzBotCs.Services
{
    public class WatermarkResult
    {
        public byte[] Bytes { get; set; } = [];
        public string ContentType { get; set; } = "application/octet-stream";
        public bool IsWatermarked { get; set; }
    }

    public class WatermarkService
    {
        private const string WatermarkText = "Грешок";

        public byte[] ApplyWatermark(byte[] imageBytes)
        {
            return ApplyWatermarkOrOriginal(imageBytes, "application/octet-stream").Bytes;
        }

        public WatermarkResult ApplyWatermarkOrOriginal(byte[] imageBytes, string originalContentType)
        {
            try
            {
                using var inputStream = new MemoryStream(imageBytes);
                using var bitmap = SKBitmap.Decode(inputStream);

                if (bitmap == null)
                {
                    return new WatermarkResult
                    {
                        Bytes = imageBytes,
                        ContentType = originalContentType,
                        IsWatermarked = false
                    };
                }

                const int maxSide = 1280;
                float scale = Math.Min(1f, maxSide / (float)Math.Max(bitmap.Width, bitmap.Height));
                int width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                int height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

                using var surface = SKSurface.Create(new SKImageInfo(width, height));
                using var canvas = surface.Canvas;

                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(bitmap, new SKRect(0, 0, width, height));

                float textSize = Math.Max(22f, width / 15f);
                using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
                using var font = new SKFont(typeface, textSize);

                using var paint = new SKPaint
                {
                    Color = SKColors.White.WithAlpha(180),
                    IsAntialias = true
                };

                using var shadowPaint = new SKPaint
                {
                    Color = SKColors.Black.WithAlpha(150),
                    IsAntialias = true,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2)
                };

                float margin = 20;
                float textWidth = font.MeasureText(WatermarkText);
                float x = width - textWidth - margin;
                float y = height - margin;

                canvas.DrawText(WatermarkText, x + 2, y + 2, font, shadowPaint);
                canvas.DrawText(WatermarkText, x, y, font, paint);

                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                
                return new WatermarkResult
                {
                    Bytes = data.ToArray(),
                    ContentType = "image/jpeg",
                    IsWatermarked = true
                };
            }
            catch
            {
                return new WatermarkResult
                {
                    Bytes = imageBytes,
                    ContentType = originalContentType,
                    IsWatermarked = false
                };
            }
        }
    }
}
