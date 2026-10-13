use regex::Regex;
use std::time::Duration;

/// Аналог Helpers/UserInputHelper.cs — чистые функции без состояния.
///
/// Ищет флаг (например "--gif") как отдельное слово в запросе.
/// При нахождении удаляет его из query и возвращает true.
pub fn try_parse_flag(query: &mut String, flag: &str) -> bool {
    let pattern = format!(r"(^|\s){}(\s|$)", regex::escape(flag));
    let re = Regex::new(&format!("(?i){}", pattern)).unwrap();
    if !re.is_match(query) {
        return false;
    }
    let replaced = re.replace_all(query, " ").trim().to_string();
    *query = replaced;
    true
}

/// Извлекает Telegram user ID из строки: чистый ID или ссылка tg://user?id=...
/// Возвращает 0, если ID не найден.
pub fn extract_user_id(input: &str) -> i64 {
    if input.trim().is_empty() {
        return 0;
    }
    let input = input.trim();

    if let Ok(id) = input.parse::<i64>() {
        return id;
    }

    const MARKER: &str = "user?id=";
    let lower = input.to_lowercase();
    if let Some(index) = lower.find(MARKER) {
        let mut id_str = &input[index + MARKER.len()..];
        if let Some(end) = id_str.find(['&', '?', ' ', '\t']) {
            id_str = &id_str[..end];
        }
        if let Ok(id) = id_str.parse::<i64>() {
            return id;
        }
    }

    0
}

/// Приводит базовый URL прокси к виду ".../img?u=". Пустой вход → None.
pub fn normalize_proxy_base_url(proxy_base_url: Option<&str>) -> Option<String> {
    let url = proxy_base_url?;
    let url = url.trim();
    if url.is_empty() {
        return None;
    }
    if url.to_lowercase().contains("?u=") {
        Some(url.to_string())
    } else {
        Some(format!("{}/img?u=", url.trim_end_matches('/')))
    }
}

/// Неиспользуемый, но сохранён для совместимости сигнатуры (таймаут в C#).
pub const DEFAULT_FLAG_REGEX_TIMEOUT: Duration = Duration::from_secs(1);
