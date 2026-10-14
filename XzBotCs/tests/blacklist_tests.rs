use std::collections::HashSet;
use xzbot::models::bot_state::{default_blacklist, find_blacklisted};

#[test]
fn blacklist_matches_case_insensitive() {
    let bl = default_blacklist();
    assert!(find_blacklisted("Детское Порно", &bl).is_some());
    assert!(find_blacklisted("child porn", &bl).is_some());
    assert!(find_blacklisted("CP collection", &bl).is_some());
}

#[test]
fn blacklist_allows_normal() {
    let bl = default_blacklist();
    assert!(find_blacklisted("котики", &bl).is_none());
    assert!(find_blacklisted("cyberpunk 2077", &bl).is_none());
    assert!(find_blacklisted("cats wallpaper", &bl).is_none());
}

#[test]
fn blacklist_custom_word() {
    let mut bl = HashSet::new();
    bl.insert("запрет".to_string());
    assert!(find_blacklisted("мой запрет", &bl).is_some());
    assert!(find_blacklisted("ок", &bl).is_none());
}
