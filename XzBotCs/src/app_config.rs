use std::collections::HashSet;
use std::fs;

/// Аналог AppConfig.cs.
#[derive(Debug, Clone)]
pub struct AppConfig {
    pub bot_token: String,
    pub admin_ids: HashSet<i64>,
    pub cache_chat_id: Option<i64>,
    pub proxy_base_url: Option<String>,
    pub proxy_port: u16,
}

pub const DEFAULT_PROXY_PORT: u16 = 8080;
pub const DEFAULT_PROXY_BASE_URL: &str = "http://46.229.63.243:8080/img?u=";
pub const DEVELOPER_PROFILE_URL: &str = "https://t.me/Tyta_Zdesyaa777";

impl AppConfig {
    pub fn load() -> Result<AppConfig, String> {
        let mut token = std::env::var("BOT_TOKEN").ok();
        let mut admin_id_str = std::env::var("ADMIN_ID").ok();
        let mut cache_chat_id_str = std::env::var("CACHE_CHAT_ID").ok();
        let mut proxy_base_url = std::env::var("PROXY_BASE_URL")
            .ok()
            .or_else(|| std::env::var("PUBLIC_BASE_URL").ok());
        let mut proxy_port_str = std::env::var("PROXY_PORT").ok();

        if let Ok(content) = fs::read_to_string("../.env") {
            for line in content.lines() {
                if line.starts_with("BOT_TOKEN=") {
                    token = Some(read_env_value(line, "BOT_TOKEN="));
                }
                if line.starts_with("ADMIN_ID=") {
                    admin_id_str = Some(read_env_value(line, "ADMIN_ID="));
                }
                if line.starts_with("CACHE_CHAT_ID=") {
                    cache_chat_id_str = Some(read_env_value(line, "CACHE_CHAT_ID="));
                }
                if line.starts_with("PROXY_BASE_URL=") {
                    proxy_base_url = Some(read_env_value(line, "PROXY_BASE_URL="));
                }
                if line.starts_with("PUBLIC_BASE_URL=") {
                    proxy_base_url = Some(read_env_value(line, "PUBLIC_BASE_URL="));
                }
                if line.starts_with("PROXY_PORT=") {
                    proxy_port_str = Some(read_env_value(line, "PROXY_PORT="));
                }
            }
        }

        let token = match token {
            Some(t) if !t.is_empty() => t,
            _ => return Err("BOT_TOKEN not found.".to_string()),
        };

        let mut admin_ids = HashSet::new();
        if let Some(ref s) = admin_id_str {
            if !s.is_empty() {
                for part in s.split(',') {
                    let part = part.trim();
                    if part.is_empty() {
                        continue;
                    }
                    if let Ok(id) = part.parse::<i64>() {
                        admin_ids.insert(id);
                    }
                }
            }
        }

        let cache_chat_id = match cache_chat_id_str
            .as_deref()
            .and_then(|s| s.parse::<i64>().ok())
        {
            Some(cid) => Some(cid),
            None => admin_ids.iter().next().copied(),
        };

        let proxy_port = proxy_port_str
            .as_deref()
            .and_then(|s| s.parse::<u16>().ok())
            .unwrap_or(DEFAULT_PROXY_PORT);

        Ok(AppConfig {
            bot_token: token,
            admin_ids,
            cache_chat_id,
            proxy_base_url: normalize_proxy_base_url(
                proxy_base_url.as_deref().unwrap_or(DEFAULT_PROXY_BASE_URL),
            ),
            proxy_port,
        })
    }
}

fn read_env_value(line: &str, key: &str) -> String {
    line[key.len()..]
        .trim()
        .trim_matches('"')
        .trim_matches('\'')
        .to_string()
}

pub fn normalize_proxy_base_url(proxy_base_url: &str) -> Option<String> {
    let trimmed = proxy_base_url.trim();
    if trimmed.is_empty() {
        return None;
    }
    if trimmed.to_lowercase().contains("?u=") {
        Some(trimmed.to_string())
    } else {
        Some(format!("{}/img?u=", trimmed.trim_end_matches('/')))
    }
}
