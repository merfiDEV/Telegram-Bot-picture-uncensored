use skia_safe::{
    BlurStyle, Canvas, Color, EncodedImageFormat, Font, FontMgr, FontStyle, Image, MaskFilter,
    Paint, Rect,
};

/// Аналог Services/WatermarkService.cs -> WatermarkResult.
pub struct WatermarkResult {
    pub bytes: Vec<u8>,
    pub content_type: String,
    pub is_watermarked: bool,
}

/// Аналог WatermarkService.
pub struct WatermarkService;

impl WatermarkService {
    pub fn new() -> Self {
        WatermarkService
    }

    pub fn apply_watermark(&self, image_bytes: &[u8], watermark_text: &str) -> Vec<u8> {
        self.apply_watermark_or_original(image_bytes, "application/octet-stream", watermark_text)
            .bytes
    }

    pub fn apply_watermark_or_original(
        &self,
        image_bytes: &[u8],
        original_content_type: &str,
        watermark_text: &str,
    ) -> WatermarkResult {
        match self.try_apply(image_bytes, watermark_text) {
            Some(bytes) => WatermarkResult {
                bytes,
                content_type: "image/jpeg".to_string(),
                is_watermarked: true,
            },
            None => WatermarkResult {
                bytes: image_bytes.to_vec(),
                content_type: original_content_type.to_string(),
                is_watermarked: false,
            },
        }
    }

    fn try_apply(&self, image_bytes: &[u8], watermark_text: &str) -> Option<Vec<u8>> {
        let data = skia_safe::Data::new_copy(image_bytes);
        let bitmap = Image::from_encoded(data)?;
        let src_w = bitmap.width();
        let src_h = bitmap.height();

        const MAX_SIDE: f32 = 1280.0;
        let max_side = src_w.max(src_h) as f32;
        let scale = (1.0f32).min(MAX_SIDE / max_side);
        let width = (1).max((src_w as f32 * scale).round() as i32);
        let height = (1).max((src_h as f32 * scale).round() as i32);

        let mut surface = skia_safe::surfaces::raster_n32_premul((width, height))?;

        {
            let canvas: &Canvas = surface.canvas();
            canvas.clear(Color::WHITE);
            let dst = Rect::from_wh(width as f32, height as f32);
            canvas.draw_image_rect(&bitmap, None, dst, &Paint::default());
        }

        let text_size = (16.0f32).max(width as f32 / 25.0);
        let mgr = FontMgr::default();
        let font = match mgr
            .legacy_make_typeface("Arial", FontStyle::bold())
            .or_else(|| mgr.legacy_make_typeface(None, FontStyle::normal()))
        {
            Some(typeface) => Font::new(typeface, Some(text_size)),
            None => Font::default(),
        };

        let mut paint = Paint::default();
        paint.set_color(Color::WHITE.with_a(180));
        paint.set_anti_alias(true);

        let mut shadow_paint = Paint::default();
        shadow_paint.set_color(Color::BLACK.with_a(150));
        shadow_paint.set_anti_alias(true);
        shadow_paint.set_mask_filter(MaskFilter::blur(BlurStyle::Normal, 2.0, None));

        let margin = 20.0f32;
        let (text_width, _) = font.measure_str(watermark_text, Some(&paint));
        let x = width as f32 - text_width - margin;
        let y = height as f32 - margin;

        {
            let canvas: &Canvas = surface.canvas();
            canvas.draw_str(watermark_text, (x + 2.0, y + 2.0), &font, &shadow_paint);
            canvas.draw_str(watermark_text, (x, y), &font, &paint);
        }

        let image = surface.image_snapshot();
        let data = image.encode(None, EncodedImageFormat::JPEG, 85)?;
        Some(data.as_bytes().to_vec())
    }
}
