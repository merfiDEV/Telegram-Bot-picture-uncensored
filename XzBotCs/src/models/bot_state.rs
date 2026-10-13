use chrono::{DateTime, Local, NaiveDateTime};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::sync::{Arc, Mutex};

/// Аналог Models/BotState.cs — поля и JSON-имена совпадают 1:1.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct BotState {
    #[serde(rename = "UsageCount")]
    pub usage_count: i32,
    #[serde(rename = "ErrorCount")]
    pub error_count: i32,
    #[serde(rename = "IsWatermarkEnabled")]
    pub is_watermark_enabled: bool,
    #[serde(rename = "WatermarkText")]
    pub watermark_text: String,
    #[serde(rename = "StartedAt")]
    pub started_at: DateTime<Local>,
    #[serde(rename = "ResponseTimesMs")]
    pub response_times_ms: Vec<f64>,
    #[serde(rename = "ResponseTimesByProvider")]
    pub response_times_by_provider: HashMap<String, Vec<f64>>,
    #[serde(rename = "ErrorDetails")]
    pub error_details: HashMap<String, i32>,
    #[serde(rename = "RecentRequests")]
    pub recent_requests: Vec<RequestRecord>,
    #[serde(rename = "WatermarkFileIds")]
    pub watermark_file_ids: HashMap<String, String>,
    #[serde(rename = "DailyUsage")]
    pub daily_usage: HashMap<String, i32>,
    #[serde(rename = "Subscribers")]
    pub subscribers: HashSet<i64>,
    #[serde(rename = "ExtraAdmins")]
    pub extra_admins: HashSet<i64>,
    #[serde(rename = "AllowNsfw")]
    pub allow_nsfw: bool,
    #[serde(rename = "UserNsfwSettings")]
    pub user_nsfw_settings: HashMap<i64, bool>,
    #[serde(rename = "SearchProvider")]
    pub search_provider: String,
    #[serde(rename = "PopularQueries")]
    pub popular_queries: HashMap<String, i32>,
    #[serde(rename = "TodayRequests")]
    pub today_requests: i32,
    #[serde(rename = "TodaySuccess")]
    pub today_success: i32,
    #[serde(rename = "TodayErrors")]
    pub today_errors: i32,
    #[serde(rename = "DashboardStates")]
    pub dashboard_states: HashMap<i64, DashboardState>,
}

impl Default for BotState {
    fn default() -> Self {
        BotState {
            usage_count: 0,
            error_count: 0,
            is_watermark_enabled: false,
            watermark_text: "Грешок by MDEV".to_string(),
            started_at: Local::now(),
            response_times_ms: Vec::new(),
            response_times_by_provider: HashMap::new(),
            error_details: HashMap::new(),
            recent_requests: Vec::new(),
            watermark_file_ids: HashMap::new(),
            daily_usage: HashMap::new(),
            subscribers: HashSet::new(),
            extra_admins: HashSet::new(),
            allow_nsfw: false,
            user_nsfw_settings: HashMap::new(),
            search_provider: "bing".to_string(),
            popular_queries: HashMap::new(),
            today_requests: 0,
            today_success: 0,
            today_errors: 0,
            dashboard_states: HashMap::new(),
        }
    }
}

pub const BOT_STATE_FILE: &str = "bot_state.json";

impl BotState {
    pub fn load() -> Self {
        if let Ok(json) = fs::read_to_string(BOT_STATE_FILE) {
            if let Ok(mut state) = serde_json::from_str::<BotState>(&json) {
                state.started_at = Local::now();
                return state;
            }
        }
        BotState::default()
    }

    /// Атомарная запись: tmp-файл → rename поверх целевого.
    pub fn save(&self) {
        let tmp = format!("{}.{}.tmp", BOT_STATE_FILE, uuid_like());
        if let Ok(json) = serde_json::to_string_pretty(self) {
            if fs::write(&tmp, json).is_ok() {
                let _ = fs::rename(&tmp, BOT_STATE_FILE);
            }
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct RequestRecord {
    #[serde(rename = "Time")]
    pub time: DateTime<Local>,
    #[serde(rename = "UserId")]
    pub user_id: i64,
    #[serde(rename = "Username")]
    pub username: String,
    #[serde(rename = "Query")]
    pub query: String,
    #[serde(rename = "Success")]
    pub success: bool,
}

impl Default for RequestRecord {
    fn default() -> Self {
        RequestRecord {
            time: Local::now(),
            user_id: 0,
            username: "Unknown".to_string(),
            query: String::new(),
            success: false,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct DashboardState {
    #[serde(rename = "Page")]
    pub page: i32,
    #[serde(rename = "Filter")]
    pub filter: String,
    #[serde(rename = "Search")]
    pub search: String,
    #[serde(rename = "AwaitingSearch")]
    pub awaiting_search: bool,
}

impl Default for DashboardState {
    fn default() -> Self {
        DashboardState {
            page: 0,
            filter: "all".to_string(),
            search: String::new(),
            awaiting_search: false,
        }
    }
}

/// Псевдо-GUID для имён tmp-файлов (аналог Guid.NewGuid().ToString("N")).
fn uuid_like() -> String {
    use rand::Rng;
    let mut rng = rand::thread_rng();
    (0..32)
        .map(|_| {
            let n: u8 = rng.gen_range(0..16);
            std::char::from_digit(n as u32, 16).unwrap()
        })
        .collect()
}

pub type SharedState = Arc<Mutex<BotState>>;

#[allow(unused_imports)]
use NaiveDateTime as _NaiveDateTimeUnused;
