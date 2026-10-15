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
    /// Рейтинги тегов: ключ — тег (поисковый запрос), значение — голоса 👍/👎.
    #[serde(rename = "TagRatings")]
    pub tag_ratings: HashMap<String, TagRating>,
    /// Кэш превью: ключ — id картинки, значение — file_id в Telegram.
    #[serde(rename = "PreviewCache")]
    pub preview_cache: HashMap<String, String>,
    /// Голоса пользователей: тег → (user_id → голос: +1 за / -1 против).
    #[serde(rename = "UserVotes")]
    pub user_votes: HashMap<String, HashMap<i64, i8>>,
    /// Данные инлайн-картинок для восстановления кнопок рейтинга: id → (тег, текст и url кнопки сайта).
    #[serde(rename = "InlineItemCache")]
    pub inline_item_cache: HashMap<String, InlineItemInfo>,
    /// Чёрный список запрещённых слов (в нижнем регистре). Запрос с таким словом отклоняется.
    #[serde(rename = "Blacklist")]
    pub blacklist: HashSet<String>,
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
            tag_ratings: HashMap::new(),
            preview_cache: HashMap::new(),
            user_votes: HashMap::new(),
            inline_item_cache: HashMap::new(),
            blacklist: default_blacklist(),
        }
    }
}

pub const BOT_STATE_FILE: &str = "bot_state.json";

/// Базовый чёрный список (CSAM-связанные термины, рус/англ).
/// Дополняется админом через /blacklist. При загрузке старого состояния список
/// тоже наполняется, если пуст.
pub fn default_blacklist() -> HashSet<String> {
    [
        // --- явные CSAM-термины ---
        "cp",
        "c.p.",
        "childporn",
        "child porn",
        "childporno",
        "child porno",
        "childsex",
        "child sex",
        "child nude",
        "childnude",
        "child naked",
        "underage",
        "underage nude",
        "underage sex",
        "loli nude",
        "lolicon nude",
        "pedo",
        "pedophile",
        "pedophilia",
        "preteen nude",
        "preteen sex",
        "minor nude",
        "minor sex",
        "jailbait nude",
        // --- русские ---
        "цп",
        "детское порно",
        "детская порнография",
        "порно дети",
        "дети порно",
        "малолетки",
        "малолетка",
        "малолетнее",
        "несовершеннолетние порно",
        "школьница порно",
        "школьницы порно",
        "лоли порно",
        "педо",
        "педофил",
        "подростки порно",
        "подросток порно",
        "малолетнее порно",
    ]
    .iter()
    .map(|s| s.to_string())
    .collect()
}

/// Проверяет, содержит ли запрос запрещённое слово из списка.
/// Возвращает само совпавшее слово (для лога), если найдено.
pub fn find_blacklisted(query: &str, blacklist: &HashSet<String>) -> Option<String> {
    let q = query.to_lowercase();
    for word in blacklist {
        if !word.is_empty() && q.contains(word.as_str()) {
            return Some(word.clone());
        }
    }
    None
}

impl BotState {
    pub fn load() -> Self {
        if let Ok(json) = fs::read_to_string(BOT_STATE_FILE) {
            if let Ok(mut state) = serde_json::from_str::<BotState>(&json) {
                state.started_at = Local::now();
                // Дополняем чёрный список базовыми терминами, если его ещё нет/пуст.
                if state.blacklist.is_empty() {
                    state.blacklist = default_blacklist();
                }
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

/// Данные инлайн-картинки: нужны, чтобы восстановить ряд «Перейти на сайт» при обновлении рейтинга.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct InlineItemInfo {
    #[serde(rename = "Tag")]
    pub tag: String,
    #[serde(rename = "BtnText")]
    pub btn_text: String,
    #[serde(rename = "BtnUrl")]
    pub btn_url: String,
}

/// Рейтинг тега: голоса за (👍) и против (👎).
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct TagRating {
    #[serde(rename = "Up")]
    pub up: i32,
    #[serde(rename = "Down")]
    pub down: i32,
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
