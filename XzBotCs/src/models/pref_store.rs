use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;

/// Аналог Models/PrefStore.cs — JSON-имя поля "Prefixes" совпадает.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct PrefStore {
    #[serde(rename = "Prefixes")]
    pub prefixes: HashMap<i64, String>,
}

impl Default for PrefStore {
    fn default() -> Self {
        PrefStore {
            prefixes: HashMap::new(),
        }
    }
}

pub const PREF_FILE: &str = "pref.json";

impl PrefStore {
    pub fn load() -> Self {
        if let Ok(json) = fs::read_to_string(PREF_FILE) {
            if let Ok(store) = serde_json::from_str::<PrefStore>(&json) {
                return store;
            }
        }
        PrefStore::default()
    }

    pub fn save(&self) {
        let tmp = format!("{}.{}.tmp", PREF_FILE, uuid_like());
        if let Ok(json) = serde_json::to_string_pretty(self) {
            if fs::write(&tmp, json).is_ok() {
                let _ = fs::rename(&tmp, PREF_FILE);
            }
        }
    }
}

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
