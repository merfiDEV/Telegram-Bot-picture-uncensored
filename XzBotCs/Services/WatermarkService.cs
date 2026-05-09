using SkiaSharp;
using System.IO;

namespace XzBotCs.Services
{
    public class WatermarkService
    {
        private const string WatermarkText = "Грешок";

        public byte[] ApplyWatermark(byte[] imageBytes)
        {
            try
            {
                using var inputStream = new MemoryStream(imageBytes);
                using var bitmap = SKBitmap.Decode(inputStream);

                if (bitmap == null) return imageBytes;

                using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height));
                using var canvas = surface.Canvas;
                
                canvas.DrawBitmap(bitmap, 0, 0);

                float textSize = bitmap.Width / 15f;
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
                float x = bitmap.Width - textWidth - margin;
                float y = bitmap.Height - margin;

                canvas.DrawText(WatermarkText, x + 2, y + 2, font, shadowPaint);
                canvas.DrawText(WatermarkText, x, y, font, paint);

                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                
                return data.ToArray();
            }
            catch
            {
                return imageBytes;
            }
        }
    }
}
