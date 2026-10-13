use xzbot::models::bot_state::BotState;
use xzbot::models::pref_store::PrefStore;

#[test]
fn bot_state_json_roundtrip() {
    // Проверяем, что сериализация даёт PascalCase-поля, как в оригинале.
    let state = BotState::default();
    let json = serde_json::to_string(&state).unwrap();
    assert!(json.contains("\"UsageCount\""));
    assert!(json.contains("\"ErrorCount\""));
    assert!(json.contains("\"IsWatermarkEnabled\""));
    assert!(json.contains("\"WatermarkText\""));
    assert!(json.contains("\"StartedAt\""));
    assert!(json.contains("\"ResponseTimesMs\""));
    assert!(json.contains("\"SearchProvider\""));
    assert!(json.contains("\"DashboardStates\""));
}

#[test]
fn bot_state_deserialize_original_format() {
    let json = r#"{
        "UsageCount": 5721,
        "ErrorCount": 82,
        "IsWatermarkEnabled": false,
        "WatermarkText": "BLURBOT BY MDEV",
        "StartedAt": "2026-10-13T12:54:40.2651579+03:00",
        "ResponseTimesMs": [666.2, 372.9],
        "ResponseTimesByProvider": {"bing": [747.6]},
        "ErrorDetails": {},
        "RecentRequests": [],
        "WatermarkFileIds": {},
        "DailyUsage": {"13.10": 5},
        "Subscribers": [1741079861],
        "ExtraAdmins": [1283137377],
        "AllowNsfw": false,
        "UserNsfwSettings": {},
        "SearchProvider": "bing",
        "PopularQueries": {},
        "TodayRequests": 0,
        "TodaySuccess": 0,
        "TodayErrors": 0,
        "DashboardStates": {}
    }"#;
    let state: BotState = serde_json::from_str(json).unwrap();
    assert_eq!(state.usage_count, 5721);
    assert_eq!(state.error_count, 82);
    assert_eq!(state.watermark_text, "BLURBOT BY MDEV");
    assert_eq!(state.search_provider, "bing");
    assert!(state.subscribers.contains(&1741079861));
    assert!(state.extra_admins.contains(&1283137377));
}

#[test]
fn pref_store_json_roundtrip() {
    let json = r#"{ "Prefixes": { "1741079861": "Адольф Гитлер", "1283137377": "Овнер" } }"#;
    let store: PrefStore = serde_json::from_str(json).unwrap();
    assert_eq!(store.prefixes.get(&1741079861).unwrap(), "Адольф Гитлер");
    assert_eq!(store.prefixes.get(&1283137377).unwrap(), "Овнер");
    let out = serde_json::to_string(&store).unwrap();
    assert!(out.contains("\"Prefixes\""));
}
