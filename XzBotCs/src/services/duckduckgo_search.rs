use crate::search_types::{BingImageResult, BingSearchResponse};
use crate::services::bing_search::get_image_hash;
use chrono::Utc;
use md5::Md5;
use regex::Regex;
use reqwest::header::{HeaderMap, HeaderValue};
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

/// Аналог Services/DuckDuckGoSearchService.cs.
pub struct DuckDuckGoSearchService {
    client: reqwest::Client,
}

const USER_AGENT: &str =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
const SEARCH_CACHE_TTL: Duration = Duration::from_secs(18 * 60);
const VQD_CACHE_TTL: Duration = Duration::from_secs(10 * 60);

struct CachedSearchResponse {
    expires_at: Instant,
    response: BingSearchResponse,
}

struct CachedVqd {
    expires_at: Instant,
    vqd: String,
}

fn search_cache() -> &'static Mutex<HashMap<String, CachedSearchResponse>> {
    static CACHE: OnceLock<Mutex<HashMap<String, CachedSearchResponse>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

fn vqd_cache() -> &'static Mutex<HashMap<String, CachedVqd>> {
    static CACHE: OnceLock<Mutex<HashMap<String, CachedVqd>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

impl DuckDuckGoSearchService {
    pub fn new() -> Self {
        let mut headers = HeaderMap::new();
        headers.insert("User-Agent", HeaderValue::from_static(USER_AGENT));
        headers.insert(
            "Accept-Language",
            HeaderValue::from_static("en-US,en;q=0.9"),
        );
        let client = reqwest::Client::builder()
            .default_headers(headers)
            .cookie_store(true)
            .timeout(Duration::from_secs(12))
            .build()
            .unwrap();
        DuckDuckGoSearchService { client }
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
        let is_gif_search = crate::services::bing_search::try_parse_gif_flag(&mut query);
        if query.trim().is_empty() {
            return BingSearchResponse {
                error_type: Some("empty_query".to_string()),
                ..Default::default()
            };
        }

        let page = 1.max((start_index + limit - 1) / 1.max(limit));

        let cache_key = build_search_cache_key(&query, page, limit, is_gif_search);
        if let Some(cached) = try_get_cached_search_response(&cache_key) {
            return cached;
        }

        let mut response = BingSearchResponse::default();
        let start = Instant::now();
        let mut collected: Vec<BingImageResult> = Vec::new();

        let strategies: Vec<(
            &str,
            fn(
                &Self,
                String,
                i32,
                i32,
                bool,
            ) -> std::pin::Pin<
                Box<
                    dyn std::future::Future<Output = Result<Vec<BingImageResult>, String>>
                        + Send
                        + '_,
                >,
            >,
        )> = vec![
            ("i.js/vqd", |s, q, p, l, g| {
                Box::pin(s.fetch_via_vqd_json_async(q, p, l, g))
            }),
            ("vqd.js", |s, q, p, l, g| {
                Box::pin(s.fetch_via_vqd_js_async(q, p, l, g))
            }),
            ("html", |s, q, p, l, g| {
                Box::pin(s.fetch_via_html_async(q, p, l, g))
            }),
            ("embedded", |s, q, p, l, g| {
                Box::pin(s.fetch_via_embedded_json_async(q, p, l, g))
            }),
        ];

        for (name, run) in strategies {
            match run(self, query.clone(), page, limit, is_gif_search).await {
                Ok(items) => {
                    if !items.is_empty() {
                        collected = items;
                        crate::log!(
                            "DuckDuckGo[{}] returned {} results for '{}'",
                            name,
                            collected.len(),
                            query
                        );
                        break;
                    }
                    crate::log!("DuckDuckGo[{}] returned 0 results for '{}'", name, query);
                }
                Err(e) => {
                    crate::log!("DuckDuckGo[{}] failed for '{}': {}", name, query, e);
                }
            }
        }

        response.items = collected;
        response.consumed_count = response.items.len();
        response.response_time = chrono::Duration::from_std(start.elapsed()).unwrap();
        if response.items.is_empty() {
            response.error_type = Some("no_results".to_string());
        }

        if response.error_type.is_none() {
            store_cached_search_response(&cache_key, &response);
        }

        response
    }

    // Стратегия 1: vqd из HTML + /i.js
    async fn fetch_via_vqd_json_async(
        &self,
        query: String,
        page: i32,
        limit: i32,
        is_gif: bool,
    ) -> Result<Vec<BingImageResult>, String> {
        let vqd = match self.get_vqd_async(&query).await? {
            Some(v) => v,
            None => return Ok(Vec::new()),
        };

        let url = format!(
            "https://duckduckgo.com/i.js?l=us-en&o=json&q={}&vqd={}&f=,,,&p={}",
            urlencoding::encode(&query),
            vqd,
            page
        );

        let body = self
            .get_string_async(
                &url,
                "https://duckduckgo.com/",
                "application/json, text/javascript, */*; q=0.01",
                true,
            )
            .await?;
        let mut items = parse_ijs_json(&body, is_gif, limit);

        if items.is_empty() && page > 1 {
            let first_url = format!(
                "https://duckduckgo.com/i.js?l=us-en&o=json&q={}&vqd={}&f=,,,&p=1",
                urlencoding::encode(&query),
                vqd
            );
            let first_body = self
                .get_string_async(
                    &first_url,
                    "https://duckduckgo.com/",
                    "application/json, text/javascript, */*; q=0.01",
                    true,
                )
                .await?;
            items = parse_ijs_json(&first_body, is_gif, limit);
        }

        Ok(items)
    }

    // Стратегия 2: vqd из /js/vqd.js
    async fn fetch_via_vqd_js_async(
        &self,
        query: String,
        page: i32,
        limit: i32,
        is_gif: bool,
    ) -> Result<Vec<BingImageResult>, String> {
        let vqd_url = format!(
            "https://duckduckgo.com/js/vqd.js?q={}&ia=images",
            urlencoding::encode(&query)
        );
        let body = self
            .get_string_async(&vqd_url, "https://duckduckgo.com/", "*/*", false)
            .await?;
        let re = Regex::new(r#"vqd\s*=\s*["']?([0-9]-[0-9a-zA-Z]+)"#).unwrap();
        let vqd = match re.captures(&body) {
            Some(c) => c[1].to_string(),
            None => return Ok(Vec::new()),
        };
        store_vqd(&query, &vqd);

        let url = format!(
            "https://duckduckgo.com/i.js?l=us-en&o=json&q={}&vqd={}&f=,,,&p={}",
            urlencoding::encode(&query),
            vqd,
            page
        );
        let json = self
            .get_string_async(
                &url,
                "https://duckduckgo.com/",
                "application/json, text/javascript, */*; q=0.01",
                true,
            )
            .await?;
        Ok(parse_ijs_json(&json, is_gif, limit))
    }

    // Стратегия 3: HTML-выдача html.duckduckgo.com
    async fn fetch_via_html_async(
        &self,
        query: String,
        _page: i32,
        limit: i32,
        is_gif: bool,
    ) -> Result<Vec<BingImageResult>, String> {
        let mut results = Vec::new();
        let mut seen = HashSet::new();

        let mut url = format!(
            "https://html.duckduckgo.com/html/?q={}",
            urlencoding::encode(&query)
        );
        if is_gif {
            url.push_str("+gif");
        }

        let html = self
            .get_string_async(
                &url,
                "https://html.duckduckgo.com/",
                "text/html,application/xhtml+xml",
                false,
            )
            .await?;
        if html.is_empty() {
            return Ok(results);
        }

        let uddg_re = Regex::new(r#"uddg=([^&"']+)"#).unwrap();
        for cap in uddg_re.captures_iter(&html) {
            let decoded = percent_decode(&cap[1]);
            if !decoded.to_lowercase().starts_with("http") {
                continue;
            }
            add_html_result(&mut results, &mut seen, &decoded, &decoded, limit);
            if results.len() >= limit as usize {
                break;
            }
        }

        if results.len() < limit as usize {
            let img_re =
                Regex::new(r#"https?://[^"'\s<>\\]+?\.(?:jpe?g|png|gif|webp)(?:\?[^"'\s<>\\]*)?"#)
                    .unwrap();
            for cap in img_re.captures_iter(&html) {
                let candidate = html_decode(cap[0].replace("\\/", "/"));
                add_html_result(&mut results, &mut seen, &candidate, &candidate, limit);
                if results.len() >= limit as usize {
                    break;
                }
            }
        }

        Ok(results)
    }

    // Стратегия 4: встроенный JSON в HTML главной страницы
    async fn fetch_via_embedded_json_async(
        &self,
        query: String,
        _page: i32,
        limit: i32,
        _is_gif: bool,
    ) -> Result<Vec<BingImageResult>, String> {
        let url = format!(
            "https://duckduckgo.com/?q={}&iax=images&ia=images",
            urlencoding::encode(&query)
        );
        let html = self
            .get_string_async(
                &url,
                "https://duckduckgo.com/",
                "text/html,application/xhtml+xml",
                false,
            )
            .await?;
        if html.is_empty() {
            return Ok(Vec::new());
        }

        let vqd_re = Regex::new(r#"vqd\s*[:=]\s*["']?([0-9]-[0-9a-zA-Z]+)"#).unwrap();
        if let Some(c) = vqd_re.captures(&html) {
            store_vqd(&query, &c[1]);
        }

        let mut results = Vec::new();
        let mut seen = HashSet::new();

        let img_re = Regex::new(r#""image"\s*:\s*"(https?:\?/\?/[^"]+)""#).unwrap();
        for cap in img_re.captures_iter(&html) {
            let image_url = cap[1].replace("\\/", "/");
            let hash = get_image_hash(&image_url);
            if !seen.insert(hash.clone()) {
                continue;
            }
            let clean = image_url.to_lowercase();
            let clean = clean.split('?').next().unwrap_or("");
            results.push(BingImageResult::new(
                image_url.clone(),
                image_url.clone(),
                image_url,
                hash,
                clean.ends_with(".gif"),
            ));
            if results.len() >= limit as usize {
                break;
            }
        }

        Ok(results)
    }

    async fn get_vqd_async(&self, query: &str) -> Result<Option<String>, String> {
        {
            let cache = vqd_cache().lock().unwrap();
            if let Some(cached) = cache.get(query) {
                if Instant::now() < cached.expires_at {
                    return Ok(Some(cached.vqd.clone()));
                }
            }
        }

        let url = format!(
            "https://duckduckgo.com/?q={}&iax=images&ia=images",
            urlencoding::encode(query)
        );
        let html = self
            .get_string_async(
                &url,
                "https://duckduckgo.com/",
                "text/html,application/xhtml+xml",
                false,
            )
            .await?;

        let re = Regex::new(r#"vqd\s*[:=]\s*["']?([0-9]-[0-9a-zA-Z]+)"#).unwrap();
        match re.captures(&html) {
            Some(c) => {
                let vqd = c[1].to_string();
                store_vqd(query, &vqd);
                Ok(Some(vqd))
            }
            None => Ok(None),
        }
    }

    async fn get_string_async(
        &self,
        url: &str,
        referer: &str,
        accept: &str,
        xhr: bool,
    ) -> Result<String, String> {
        let mut req = self
            .client
            .get(url)
            .header("Referer", referer)
            .header("Accept", accept)
            .header("Accept-Language", "en-US,en;q=0.9")
            .header("Sec-Fetch-Dest", if xhr { "empty" } else { "document" })
            .header("Sec-Fetch-Mode", if xhr { "cors" } else { "navigate" })
            .header("Sec-Fetch-Site", "same-origin");
        if xhr {
            req = req.header("X-Requested-With", "XMLHttpRequest");
        }

        let resp = req.send().await.map_err(|e| e.to_string())?;
        if !resp.status().is_success() {
            return Err(format!("DuckDuckGo {} for {}", resp.status().as_u16(), url));
        }
        resp.text().await.map_err(|e| e.to_string())
    }

    pub async fn check_duckduckgo(&self) -> (bool, String) {
        let req = self
            .client
            .get("https://duckduckgo.com/")
            .header("Accept", "text/html,application/xhtml+xml");
        match tokio::time::timeout(std::time::Duration::from_secs(4), req.send()).await {
            Ok(Ok(resp)) => (
                resp.status().is_success(),
                resp.status().as_u16().to_string(),
            ),
            Ok(Err(e)) => {
                let name = if e.is_timeout() {
                    "TaskCanceledException"
                } else {
                    "HttpRequestException"
                };
                (false, name.to_string())
            }
            Err(_) => (false, "TaskCanceledException".to_string()),
        }
    }
}

fn add_html_result(
    results: &mut Vec<BingImageResult>,
    seen: &mut HashSet<String>,
    image_url: &str,
    source_url: &str,
    limit: i32,
) {
    if results.len() >= limit as usize {
        return;
    }
    if image_url.trim().is_empty() {
        return;
    }
    if image_url.contains('<') || image_url.contains('>') || image_url.contains('"') {
        return;
    }
    if image_url.contains("duckduckgo.com") {
        return;
    }

    let hash = get_image_hash(image_url);
    if !seen.insert(hash.clone()) {
        return;
    }

    let clean = image_url.to_lowercase();
    let clean = clean.split('?').next().unwrap_or("");
    results.push(BingImageResult::new(
        image_url.to_string(),
        image_url.to_string(),
        source_url.to_string(),
        hash,
        clean.ends_with(".gif"),
    ));
}

fn parse_ijs_json(body: &str, is_gif: bool, limit: i32) -> Vec<BingImageResult> {
    let mut results = Vec::new();
    if body.trim().is_empty() {
        return results;
    }

    let mut json = body.trim().to_string();
    if !json.starts_with('{') && !json.starts_with('[') {
        match json.find('{') {
            Some(brace) => json = json[brace..].to_string(),
            None => return results,
        }
    }

    let value: Value = match serde_json::from_str(&json) {
        Ok(v) => v,
        Err(e) => {
            crate::log!("DuckDuckGo JSON parse error: {}", e);
            return results;
        }
    };

    let arr = match value.get("results").and_then(|v| v.as_array()) {
        Some(a) => a,
        None => return results,
    };

    let mut seen = HashSet::new();
    for item in arr {
        let image = get_json_string(item, "image");
        if image.is_empty() {
            continue;
        }
        if !image.to_lowercase().starts_with("http") {
            continue;
        }

        let mut thumbnail = get_json_string(item, "thumbnail");
        if thumbnail.is_empty() {
            thumbnail = image.clone();
        }

        let mut source = get_json_string(item, "url");
        if source.is_empty() {
            source = image.clone();
        }

        let item_type = get_json_string(item, "type");
        let clean_image = image.to_lowercase();
        let clean_image = clean_image.split('?').next().unwrap_or("").to_string();

        let is_real_gif = item_type.to_lowercase().contains("gif") || clean_image.ends_with(".gif");
        if is_gif && !is_real_gif {
            continue;
        }

        let hash = get_image_hash(&image);
        if !seen.insert(hash.clone()) {
            continue;
        }

        results.push(BingImageResult::new(
            image,
            thumbnail,
            source,
            hash,
            is_real_gif,
        ));
        if results.len() >= limit as usize {
            break;
        }
    }

    results
}

fn get_json_string(element: &Value, property: &str) -> String {
    element
        .get(property)
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string()
}

fn store_vqd(query: &str, vqd: &str) {
    let mut cache = vqd_cache().lock().unwrap();
    cache.insert(
        query.to_string(),
        CachedVqd {
            expires_at: Instant::now() + VQD_CACHE_TTL,
            vqd: vqd.to_string(),
        },
    );
}

fn build_search_cache_key(query: &str, page: i32, limit: i32, is_gif: bool) -> String {
    format!(
        "{}|{}|{}|{}",
        query.trim().to_lowercase(),
        if is_gif { "gif" } else { "img" },
        page,
        limit
    )
}

fn try_get_cached_search_response(cache_key: &str) -> Option<BingSearchResponse> {
    let mut cache = search_cache().lock().unwrap();
    if let Some(cached) = cache.get(cache_key) {
        if Instant::now() < cached.expires_at {
            return Some(crate::services::bing_search::clone_search_response(
                &cached.response,
            ));
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
            response: crate::services::bing_search::clone_search_response(response),
        },
    );
}

fn html_decode(s: String) -> String {
    let mut out = s;
    out = out.replace("&amp;", "&");
    out = out.replace("&quot;", "\u{22}");
    out = out.replace("&#39;", "\u{27}");
    out = out.replace("&lt;", "<");
    out = out.replace("&gt;", ">");
    out
}

fn percent_decode(s: &str) -> String {
    urlencoding::decode(s)
        .map(|c| c.into_owned())
        .unwrap_or_else(|_| s.to_string())
}

#[allow(unused_imports)]
use {Md5 as _md5, Utc as _utc};
