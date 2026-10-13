use std::sync::{Arc, Mutex};
use xzbot::models::bot_state::BotState;
use xzbot::services::bot_stats::BotStatsService;

#[test]
fn build_stats_text_does_not_panic() {
    let state = Arc::new(Mutex::new(BotState::default()));
    let svc = BotStatsService::new(state);
    let statuses = vec![
        (
            "bing".to_string(),
            "Bing".to_string(),
            true,
            "200".to_string(),
        ),
        (
            "ddg".to_string(),
            "DuckDuckGo".to_string(),
            false,
            "Timeout".to_string(),
        ),
    ];
    let text = svc.build_stats_text(&statuses);
    println!(
        "STATS TEXT:
{}",
        text
    );
    assert!(text.contains("Статистика бота"));
}

#[test]
fn generate_chart_does_not_panic() {
    let state = Arc::new(Mutex::new(BotState::default()));
    let svc = BotStatsService::new(state);
    let bytes = svc.generate_chart_image();
    println!("chart bytes: {}", bytes.len());
    assert!(!bytes.is_empty());
}

#[test]
fn build_metrics_text_does_not_panic() {
    let state = Arc::new(Mutex::new(BotState::default()));
    let svc = BotStatsService::new(state);
    let text = svc.build_metrics_text();
    println!(
        "METRICS:
{}",
        text
    );
    assert!(text.contains("Метрики"));
}

#[test]
fn build_dashboard_text_does_not_panic() {
    let state = Arc::new(Mutex::new(BotState::default()));
    let svc = BotStatsService::new(state);
    let text = svc.build_dashboard_text(123, None, None, None);
    println!(
        "DASHBOARD:
{}",
        text
    );
    assert!(text.contains("Дашборд"));
}
