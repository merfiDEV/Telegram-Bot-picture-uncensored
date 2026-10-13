// Проверяет, что все regex-паттерны проекта компилируются.
// Защита от повреждения экранирования при записи файлов.
use regex::Regex;

#[test]
fn bing_patterns_compile() {
    Regex::new(r#"m="(\{.*?\})""#).unwrap();
    Regex::new(r#"murl&quot;:&quot;(.*?)&quot;"#).unwrap();
    Regex::new(r#"turl&quot;:&quot;(.*?)&quot;"#).unwrap();
    Regex::new(r#"purl&quot;:&quot;(.*?)&quot;"#).unwrap();
    Regex::new(r"(?i)(^|\s)--gif(\s|$)").unwrap();
}

#[test]
fn ddg_patterns_compile() {
    Regex::new(r#"vqd\s*=\s*["']?([0-9]-[0-9a-zA-Z]+)"#).unwrap();
    Regex::new(r#"vqd\s*[:=]\s*["']?([0-9]-[0-9a-zA-Z]+)"#).unwrap();
    Regex::new(r#"uddg=([^&"']+)"#).unwrap();
    Regex::new(r#"https?://[^"'\s<>\\]+?\.(?:jpe?g|png|gif|webp)(?:\?[^"'\s<>\\]*)?"#).unwrap();
    Regex::new(r#""image"\s*:\s*"(https?:\\?/\\?/[^"]+)""#).unwrap();
}

#[test]
fn user_input_pattern_compiles() {
    let pattern = format!(r"(^|\s){}(\s|$)", regex::escape("--gif"));
    Regex::new(&format!("(?i){}", pattern)).unwrap();
}
