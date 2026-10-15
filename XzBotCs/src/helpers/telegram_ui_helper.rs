use crate::search_types::BingImageResult;
use crate::services::search_provider_registry::DUCKDUCKGO_KEY;
use teloxide::types::{InlineKeyboardButton, InlineKeyboardMarkup};

/// Аналог Helpers/TelegramUiHelper.cs.

pub fn build_stats_markup(
    is_watermark_enabled: bool,
    active_provider_key: &str,
) -> InlineKeyboardMarkup {
    let wm_btn_text = if is_watermark_enabled {
        "❌ Выключить ватермарку"
    } else {
        "✅ Включить ватермарку"
    };
    let provider_label = if active_provider_key == DUCKDUCKGO_KEY {
        "🔎 DDG"
    } else {
        "🔎 Bing"
    };

    InlineKeyboardMarkup::new(vec![
        vec![
            InlineKeyboardButton::callback("📈 Метрики", "stats:metrics"),
            InlineKeyboardButton::callback("📋 Дашборд", "stats:dashboard"),
        ],
        vec![InlineKeyboardButton::callback(wm_btn_text, "toggle_wm")],
        vec![
            InlineKeyboardButton::callback(provider_label, "toggle_provider"),
            InlineKeyboardButton::callback("🔄 Обновить", "stats:refresh"),
        ],
    ])
}

/// Разметка под фото в чате: доминирующий ряд 👍/👎 + «Перейти на сайт» внизу.
/// Тег (обычно поисковый запрос) кодируется в callback для сбора оценок.
pub fn photo_markup_with_rating(
    item: &BingImageResult,
    tag: &str,
    up: i32,
    down: i32,
) -> InlineKeyboardMarkup {
    let (btn_text, btn_url) = source_button(item);
    // Тег может содержать произвольные символы — base64url без паддинга, чтобы уложиться в лимит callback (64 байта).
    use base64::Engine;
    let tag_enc = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(tag.as_bytes());
    InlineKeyboardMarkup::new(vec![
        vec![
            InlineKeyboardButton::callback(format!("👍 {}", up), format!("rate:up:{}", tag_enc)),
            InlineKeyboardButton::callback(
                format!("👎 {}", down),
                format!("rate:down:{}", tag_enc),
            ),
        ],
        vec![InlineKeyboardButton::url(
            btn_text,
            btn_url.parse().unwrap(),
        )],
    ])
}

/// Инлайн-разметка: ряд 👍/👎 (callback несёт id картинки) + «Перейти на сайт».
/// item_id используется и как ключ в inline_item_cache, и внутри callback_data.
pub fn inline_markup_with_rating(
    item: &BingImageResult,
    item_id: &str,
    up: i32,
    down: i32,
) -> InlineKeyboardMarkup {
    let (btn_text, btn_url) = source_button(item);
    use base64::Engine;
    let id_enc = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(item_id.as_bytes());
    InlineKeyboardMarkup::new(vec![
        vec![
            InlineKeyboardButton::callback(format!("👍 {}", up), format!("rate:up:{}", id_enc)),
            InlineKeyboardButton::callback(format!("👎 {}", down), format!("rate:down:{}", id_enc)),
        ],
        vec![InlineKeyboardButton::url(
            btn_text,
            btn_url.parse().unwrap(),
        )],
    ])
}

pub fn source_button(item: &BingImageResult) -> (String, String) {
    let has_http = item.source_url.to_lowercase().starts_with("http");
    let url = if has_http {
        item.source_url.clone()
    } else {
        item.url.clone()
    };
    let text = if has_http {
        "🌐 Перейти на сайт".to_string()
    } else {
        "🖼 Открыть оригинал".to_string()
    };
    (text, url)
}
