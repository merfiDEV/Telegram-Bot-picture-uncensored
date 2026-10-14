use crate::search_types::{BingImageResult, BingSearchResponse};
use chrono::{Duration as ChronoDuration, Utc};
use md5::{Digest, Md5};
use regex::Regex;
use reqwest::header::{HeaderMap, HeaderValue};
use std::collections::{HashMap, HashSet};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

/// Аналог Services/BingSearchService.cs.
pub struct BingSearchService {
    client: reqwest::Client,
}

#[allow(dead_code)]
const IMAGE_METADATA_TIMEOUT: Duration = Duration::from_millis(650);
#[allow(dead_code)]
const REGEX_TIMEOUT: Duration = Duration::from_secs(2);
const SEARCH_CACHE_TTL: Duration = Duration::from_secs(18 * 60);

struct CachedSearchResponse {
    expires_at: Instant,
    response: BingSearchResponse,
}

fn search_cache() -> &'static Mutex<HashMap<String, CachedSearchResponse>> {
    static CACHE: OnceLock<Mutex<HashMap<String, CachedSearchResponse>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

impl BingSearchService {
    pub fn new() -> Self {
        let mut headers = HeaderMap::new();
        headers.insert(
            "User-Agent",
            HeaderValue::from_static(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
            ),
        );
        let client = reqwest::Client::builder()
            .default_headers(headers)
            .cookie_store(true)
            .build()
            .unwrap();
        BingSearchService { client }
    }

    pub async fn search_images(
        &self,
        query: &str,
        start_index: i32,
        limit: i32,
    ) -> Vec<BingImageResult> {
        self.search_images_detailed(query, start_index, limit)
            .await
            .items
    }

    pub async fn search_images_detailed(
        &self,
        query: &str,
        start_index: i32,
        limit: i32,
    ) -> BingSearchResponse {
        let mut query = query.to_string();
        let is_gif_search = try_parse_gif_flag(&mut query);

        let cache_key = build_search_cache_key(&query, start_index, limit, is_gif_search);
        if let Some(cached) = try_get_cached_search_response(&cache_key) {
            return cached;
        }

        let mut search_response = BingSearchResponse::default();
        let start = Instant::now();

        let result: Result<(), (String, String)> = async {
            if is_gif_search {
                let filtered = self
                    .fetch_page_async(
                        &query,
                        start_index,
                        limit,
                        Some("%2Bfilterui%3Aphoto-animatedgif"),
                    )
                    .await
                    .map_err(|e| ("http_error".to_string(), e))?;
                search_response.items.extend(filtered);

                if search_response.items.is_empty() {
                    crate::log!(
                        "GIF search returned 0 with filter, trying fallback for '{}'",
                        query
                    );
                    let plain = self
                        .fetch_page_async(&query, start_index, limit, None)
                        .await
                        .map_err(|e| ("http_error".to_string(), e))?;
                    for mut item in plain {
                        let lower = item.url.to_lowercase();
                        let clean = lower.split('?').next().unwrap_or("");
                        if clean.ends_with(".gif") {
                            item.is_gif = true;
                            search_response.items.push(item);
                            if search_response.items.len() >= limit as usize {
                                break;
                            }
                        }
                    }
                }
            } else {
                let fetched = self
                    .fetch_page_async(&query, start_index, limit, None)
                    .await
                    .map_err(|e| ("http_error".to_string(), e))?;
                search_response.items.extend(fetched);
            }
            Ok(())
        }
        .await;

        if let Err((err_type, msg)) = result {
            search_response.error_type = Some(err_type);
            crate::log!("Search error: {}", msg);
        }

        search_response.response_time = chrono::Duration::from_std(start.elapsed()).unwrap();
        search_response.consumed_count = search_response.items.len();

        if search_response.error_type.is_none() {
            store_cached_search_response(&cache_key, &search_response);
        }

        search_response
    }

    async fn fetch_page_async(
        &self,
        query: &str,
        start_index: i32,
        limit: i32,
        qft_filter: Option<&str>,
    ) -> Result<Vec<BingImageResult>, String> {
        let mut results = Vec::new();
        let encoded_query = urlencoding::encode(query);
        let mut fetch_url = format!(
            "https://www.bing.com/images/search?q={}&adlt=off&safeSearch=Off&setmkt=en-US&setlang=en-US&first={}&count={}",
            encoded_query, start_index, limit
        );
        if let Some(qft) = qft_filter {
            if !qft.is_empty() {
                fetch_url.push_str(&format!("&qft={}", qft));
            }
        }

        let resp = self
            .client
            .get(&fetch_url)
            .header("Cookie", "SRCHHPGUSR=ADLT=OFF&NRSLT=50")
            .header("Accept-Language", "en-US,en;q=0.9")
            .send()
            .await
            .map_err(|e| e.to_string())?;
        let resp = resp.error_for_status().map_err(|e| e.to_string())?;
        let html = resp.text().await.map_err(|e| e.to_string())?;

        let block_re = Regex::new(r#"m="(\{.*?\})""#).unwrap();
        let seen_hashes = std::cell::RefCell::new(HashSet::new());
        let gif_by_filter = qft_filter.map(|f| !f.is_empty()).unwrap_or(false);

        for cap in block_re.captures_iter(&html) {
            let block = &cap[1];

            let murl_re = Regex::new(r#"murl&quot;:&quot;(.*?)&quot;"#).unwrap();
            // [^a-zA-Z] перед turl — чтобы не матчить "cturl&quot;:&quot;" (пустое значение).
            let turl_re = Regex::new(r#"[^a-zA-Z]turl&quot;:&quot;(.*?)&quot;"#).unwrap();
            let purl_re = Regex::new(r#"purl&quot;:&quot;(.*?)&quot;"#).unwrap();

            let murl_match = match murl_re.captures(block) {
                Some(c) => c,
                None => continue,
            };

            let murl = html_decode(murl_match[1].replace("\\/", "/"));
            let turl = turl_re
                .captures(block)
                .map(|c| html_decode(c[1].replace("\\/", "/")))
                .unwrap_or_else(|| murl.clone());
            let purl = purl_re
                .captures(block)
                .map(|c| html_decode(c[1].replace("\\/", "/")))
                .unwrap_or_default();

            if !murl.starts_with("http") {
                continue;
            }
            if murl.contains('<') || murl.contains('>') || murl.contains('"') || murl.contains(' ')
            {
                continue;
            }

            let lower = murl.to_lowercase();
            let clean = lower.split('?').next().unwrap_or("");
            let is_gif = clean.ends_with(".gif");
            if gif_by_filter && !is_gif {
                continue;
            }

            let image_hash = get_image_hash(&murl);
            if !seen_hashes.borrow_mut().insert(image_hash.clone()) {
                continue;
            }

            results.push(BingImageResult::new(murl, turl, purl, image_hash, is_gif));

            if results.len() >= limit as usize {
                break;
            }
        }

        Ok(results)
    }

    pub async fn check_bing(&self) -> (bool, String) {
        let fut = self.client.get("https://www.bing.com").send();
        match tokio::time::timeout(std::time::Duration::from_secs(3), fut).await {
            Ok(Ok(resp)) => {
                let ok = resp.status().is_success();
                (ok, resp.status().as_u16().to_string())
            }
            Ok(Err(e)) => (false, err_name(&e)),
            Err(_) => (false, "TimeoutException".to_string()),
        }
    }
}

fn err_name(e: &reqwest::Error) -> String {
    if e.is_timeout() {
        "TimeoutException".to_string()
    } else if e.is_connect() {
        "HttpRequestException".to_string()
    } else {
        "Exception".to_string()
    }
}

pub fn try_parse_gif_flag(query: &mut String) -> bool {
    let re = Regex::new(r"(?i)(^|\s)--gif(\s|$)").unwrap();
    if !re.is_match(query) {
        return false;
    }
    *query = re.replace_all(query, " ").trim().to_string();
    true
}

pub fn get_image_hash(url: &str) -> String {
    let clean = url
        .split('?')
        .next()
        .unwrap_or(url)
        .split('#')
        .next()
        .unwrap_or(url)
        .to_lowercase();
    let clean = clean.trim();
    let mut hasher = Md5::new();
    hasher.update(clean.as_bytes());
    format!("{:x}", hasher.finalize())
}

fn build_search_cache_key(query: &str, start_index: i32, limit: i32, is_gif: bool) -> String {
    format!(
        "{}|{}|{}|{}",
        query.trim().to_lowercase(),
        if is_gif { "gif" } else { "img" },
        start_index,
        limit
    )
}

fn try_get_cached_search_response(cache_key: &str) -> Option<BingSearchResponse> {
    let mut cache = search_cache().lock().unwrap();
    if let Some(cached) = cache.get(cache_key) {
        if Instant::now() < cached.expires_at {
            return Some(clone_search_response(&cached.response));
        }
    }
    cache.remove(cache_key);
    None
}

fn store_cached_search_response(cache_key: &str, response: &BingSearchResponse) {
    let mut cache = search_cache().lock().unwrap();
    cache.insert(
        cache_key.to_string(),
        CachedSearchResponse {
            expires_at: Instant::now() + SEARCH_CACHE_TTL,
            response: clone_search_response(response),
        },
    );
}

pub fn clone_search_response(response: &BingSearchResponse) -> BingSearchResponse {
    BingSearchResponse {
        consumed_count: response.consumed_count,
        response_time: response.response_time,
        error_type: response.error_type.clone(),
        items: response
            .items
            .iter()
            .map(|item| BingImageResult {
                url: item.url.clone(),
                thumbnail_url: item.thumbnail_url.clone(),
                source_url: item.source_url.clone(),
                id: item.id.clone(),
                is_gif: item.is_gif,
            })
            .collect(),
    }
}

pub fn html_decode(s: String) -> String {
    let mut out = s;
    out = out.replace("&amp;", "&");
    out = out.replace("&quot;", "\"");
    out = out.replace("&#39;", "'");
    out = out.replace("&lt;", "<");
    out = out.replace("&gt;", ">");
    out
}

#[allow(unused_imports)]
use {ChronoDuration as _cd, Utc as _utc};
