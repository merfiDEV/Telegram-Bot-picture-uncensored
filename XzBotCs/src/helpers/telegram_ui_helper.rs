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
