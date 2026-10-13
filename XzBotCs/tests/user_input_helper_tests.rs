use xzbot::helpers::user_input_helper::{
    extract_user_id, normalize_proxy_base_url, try_parse_flag,
};

#[test]
fn flag_at_end_removed_and_true() {
    let mut query = "кот --random".to_string();
    assert!(try_parse_flag(&mut query, "--random"));
    assert_eq!("кот", query);
}

#[test]
fn flag_at_start_removed_and_true() {
    let mut query = "--gif dance".to_string();
    assert!(try_parse_flag(&mut query, "--gif"));
    assert_eq!("dance", query);
}

#[test]
fn flag_in_middle_removed_and_true() {
    let mut query = "кошка --gif спит".to_string();
    assert!(try_parse_flag(&mut query, "--gif"));
    assert_eq!("кошка спит", query);
}

#[test]
fn no_flag_returns_false_and_keeps_query() {
    let mut query = "обычный запрос".to_string();
    assert!(!try_parse_flag(&mut query, "--random"));
    assert_eq!("обычный запрос", query);
}

#[test]
fn substring_of_word_not_matched() {
    let mut query = "abc--randomxyz".to_string();
    assert!(!try_parse_flag(&mut query, "--random"));
}

#[test]
fn case_insensitive() {
    let mut query = "кот --RANDOM".to_string();
    assert!(try_parse_flag(&mut query, "--random"));
    assert_eq!("кот", query);
}

#[test]
fn extract_user_id_valid() {
    assert_eq!(1741079861, extract_user_id("1741079861"));
    assert_eq!(1741079861, extract_user_id("  1741079861  "));
    assert_eq!(1741079861, extract_user_id("tg://user?id=1741079861"));
    assert_eq!(
        1741079861,
        extract_user_id("tg://user?id=1741079861&foo=bar")
    );
}

#[test]
fn extract_user_id_invalid() {
    assert_eq!(0, extract_user_id(""));
    assert_eq!(0, extract_user_id("   "));
    assert_eq!(0, extract_user_id("not-a-number"));
    assert_eq!(0, extract_user_id("tg://user?id=abc"));
}

#[test]
fn normalize_proxy_null_or_empty() {
    assert_eq!(None, normalize_proxy_base_url(None));
    assert_eq!(None, normalize_proxy_base_url(Some("")));
    assert_eq!(None, normalize_proxy_base_url(Some("   ")));
}

#[test]
fn normalize_proxy_already_has_query() {
    assert_eq!(
        Some("https://example.com/img?u=".to_string()),
        normalize_proxy_base_url(Some("https://example.com/img?u="))
    );
}

#[test]
fn normalize_proxy_with_trailing_slash() {
    assert_eq!(
        Some("https://example.com/img?u=".to_string()),
        normalize_proxy_base_url(Some("https://example.com/"))
    );
}

#[test]
fn normalize_proxy_without_trailing_slash() {
    assert_eq!(
        Some("https://example.com/img?u=".to_string()),
        normalize_proxy_base_url(Some("https://example.com"))
    );
}

#[test]
fn normalize_proxy_trims_whitespace() {
    assert_eq!(
        Some("https://example.com/img?u=".to_string()),
        normalize_proxy_base_url(Some("  https://example.com  "))
    );
}
