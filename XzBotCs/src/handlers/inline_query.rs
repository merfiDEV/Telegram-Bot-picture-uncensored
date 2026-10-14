use crate::helpers::telegram_escaper::escape_markdown_v2;
use crate::helpers::user_input_helper::try_parse_flag;
use crate::models::bot_state::SharedState;
use crate::search_types::BingImageResult;
use crate::services::bot_stats::BotStatsService;
use crate::services::search_provider_registry::{RoutingSearchService, SearchProviderRegistry};
use crate::services::watermark::WatermarkService;
use base64::Engine;
use std::collections::HashSet;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use teloxide::prelude::*;
use teloxide::types::{
    ChatId, InlineQuery, InlineQueryResult, InlineQueryResultArticle, InlineQueryResultCachedPhoto,
    InlineQueryResultGif, InlineQueryResultPhoto, InputMessageContent, InputMessageContentText,
    ParseMode,
};

/// Аналог Handlers/InlineQueryService.cs.
pub struct InlineQueryService {
    state: SharedState,
    admin_ids: Arc<HashSet<i64>>,
    stats_service: Arc<BotStatsService>,
    search_service: Arc<RoutingSearchService>,
    search_registry: Arc<SearchProviderRegistry>,
    watermark_service: Arc<WatermarkService>,
    http_client: reqwest::Client,
    watermark_upload_lock: Arc<tokio::sync::Semaphore>,
    pub cache_chat_id: Option<i64>,
    pub proxy_base_url: Option<String>,
    pub proxy_listener_started: Arc<AtomicBool>,
    pub bot_username: Mutex<Option<String>>,
}

impl InlineQueryService {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        state: SharedState,
        admin_ids: Arc<HashSet<i64>>,
        stats_service: Arc<BotStatsService>,
        search_service: Arc<RoutingSearchService>,
        search_registry: Arc<SearchProviderRegistry>,
        watermark_service: Arc<WatermarkService>,
        http_client: reqwest::Client,
        watermark_upload_lock: Arc<tokio::sync::Semaphore>,
        cache_chat_id: Option<i64>,
        proxy_base_url: Option<String>,
        proxy_listener_started: Arc<AtomicBool>,
    ) -> Self {
        InlineQueryService {
            state,
            admin_ids,
            stats_service,
            search_service,
            search_registry,
            watermark_service,
            http_client,
            watermark_upload_lock,
            cache_chat_id,
            proxy_base_url,
            proxy_listener_started,
            bot_username: Mutex::new(None),
        }
    }

    pub async fn handle(&self, bot: &Bot, inline_query: InlineQuery) -> ResponseResult<()> {
        let query = inline_query.query.trim().to_string();
        if query.is_empty() {
            let result = InlineQueryResultArticle::new(
                "empty-query",
                "🔍 Введите запрос",
                InputMessageContent::Text(InputMessageContentText::new(
                    "Введите запрос после имени бота, и я найду картинки.",
                )),
            )
            .description("Напишите, какую картинку найти. Например: кот в очках");
            let _ = bot
                .answer_inline_query(inline_query.id.clone(), vec![result.into()])
                .cache_time(0)
                .await;
            return Ok(());
        }

        let from_id = inline_query.from.id.0 as i64;
        let is_subscriber = {
            let st = self.state.lock().unwrap();
            st.subscribers.contains(&from_id)
        };
        if !is_subscriber && !self.admin_ids.contains(&from_id) {
            let bot_username = self.bot_username.lock().unwrap().clone();
            let bot_url = bot_username.map(|u| format!("https://t.me/{}?start=register", u));
            let mut result = InlineQueryResultArticle::new(
                "register-required",
                "🔒 Подтвердите регистрацию",
                InputMessageContent::Text(InputMessageContentText::new(
                    "Для использования бота откройте личные сообщения и подтвердите регистрацию.",
                )),
            );
            result = result.description("Нажмите, чтобы перейти в ЛС с ботом");
            if let Some(url) = bot_url {
                if let Ok(u) = url.parse() {
                    result = result.reply_markup(teloxide::types::InlineKeyboardMarkup::new(vec![
                        vec![teloxide::types::InlineKeyboardButton::url(
                            "✅ Перейти в ЛС",
                            u,
                        )],
                    ]));
                }
            }
            let _ = bot
                .answer_inline_query(inline_query.id.clone(), vec![result.into()])
                .cache_time(0)
                .is_personal(true)
                .await;
            return Ok(());
        }

        if query.to_lowercase().starts_with("/logs") {
            return self.handle_logs(bot, inline_query).await;
        }

        let mut query_mut = query.clone();
        let is_random = try_parse_flag(&mut query_mut, "--random");
        let offset: i32 = inline_query.offset.parse().unwrap_or(0);
        crate::log!(
            "Inline query from {}: '{}', offset={}, random={}",
            from_id,
            query_mut,
            offset,
            is_random
        );

        self.stats_service.increment_usage();

        let wm_enabled = {
            let st = self.state.lock().unwrap();
            st.is_watermark_enabled
        };
        let result_limit = if wm_enabled { 6 } else { 30 };
        let search_response = self
            .search_service
            .search_images_detailed(&query_mut, offset + 1, result_limit)
            .await;

        if !search_response.response_time.is_zero() {
            self.stats_service
                .record_response_time(search_response.response_time);
            self.stats_service.record_response_time_provider(
                &self.search_registry.active_key(),
                search_response.response_time,
            );
        }
        if let Some(ref err) = search_response.error_type {
            self.stats_service.record_error(err);
        }

        let search_results = search_response.items;
        crate::log!(
            "Search returned {} results for '{}'",
            search_results.len(),
            query_mut
        );

        let selected_items: Vec<BingImageResult> = if is_random && search_results.len() > 1 {
            use rand::Rng;
            let idx = rand::thread_rng().gen_range(0..search_results.len());
            vec![search_results[idx].clone()]
        } else {
            search_results.clone()
        };

        let mut results: Vec<InlineQueryResult> = Vec::new();
        for item in &selected_items {
            let mut final_url = item.url.clone();
            let thumbnail_url = if item.thumbnail_url.is_empty() {
                item.url.clone()
            } else {
                item.thumbnail_url.clone()
            };

            if wm_enabled && !item.is_gif {
                if let Some(file_id) = self
                    .get_or_upload_watermarked_photo_file_id(bot, item)
                    .await
                {
                    let mut r = InlineQueryResultCachedPhoto::new(item.id.clone(), file_id);
                    r = r.reply_markup(source_markup(item));
                    results.push(r.into());
                    continue;
                }
                final_url = self.build_proxy_image_url(&item.url);
            }

            let markup = source_markup(item);
            let final_url_parsed = final_url.parse().unwrap();
            let thumb_parsed = thumbnail_url.parse().unwrap();
            if item.is_gif {
                let mut r =
                    InlineQueryResultGif::new(item.id.clone(), final_url_parsed, thumb_parsed);
                r = r.reply_markup(markup);
                results.push(r.into());
            } else {
                let mut r =
                    InlineQueryResultPhoto::new(item.id.clone(), final_url_parsed, thumb_parsed);
                r = r.reply_markup(markup);
                results.push(r.into());
            }
        }

        let next_offset = if !search_results.is_empty() {
            (offset + search_results.len() as i32).to_string()
        } else {
            String::new()
        };

        let send_result = bot
            .answer_inline_query(inline_query.id.clone(), results)
            .cache_time(300)
            .next_offset(next_offset)
            .await;

        let answered = send_result.is_ok();
        self.stats_service.record_request(
            from_id,
            inline_query.from.username.as_deref(),
            &query_mut,
            answered && !search_results.is_empty() && search_response.error_type.is_none(),
        );

        Ok(())
    }

    async fn handle_logs(&self, bot: &Bot, inline_query: InlineQuery) -> ResponseResult<()> {
        let from_id = inline_query.from.id.0 as i64;
        if !self.admin_ids.contains(&from_id) {
            let result = InlineQueryResultArticle::new(
                "no-access",
                "⛔ Нет доступа",
                InputMessageContent::Text(InputMessageContentText::new(
                    "У вас нет доступа к логам.",
                )),
            );
            let _ = bot
                .answer_inline_query(inline_query.id.clone(), vec![result.into()])
                .cache_time(0)
                .is_personal(true)
                .await;
            return Ok(());
        }

        let dashboard_text = self
            .stats_service
            .build_dashboard_text(from_id, None, None, None);
        let result = InlineQueryResultArticle::new(
            "logs-dashboard",
            "📋 Логи запросов",
            InputMessageContent::Text(
                InputMessageContentText::new(dashboard_text.clone())
                    .parse_mode(ParseMode::MarkdownV2),
            ),
        )
        .description("Последние 10 запросов");

        let send = bot
            .answer_inline_query(inline_query.id.clone(), vec![result.into()])
            .cache_time(10)
            .is_personal(true)
            .await;

        if send.is_err() {
            let fallback = InlineQueryResultArticle::new(
                "logs-dashboard",
                "📋 Логи запросов",
                InputMessageContent::Text(InputMessageContentText::new(dashboard_text)),
            )
            .description("Последние 10 запросов");
            let _ = bot
                .answer_inline_query(inline_query.id.clone(), vec![fallback.into()])
                .cache_time(10)
                .is_personal(true)
                .await;
        }
        Ok(())
    }

    fn build_proxy_image_url(&self, image_url: &str) -> String {
        if self.proxy_base_url.is_none() || !self.proxy_listener_started.load(Ordering::SeqCst) {
            return image_url.to_string();
        }
        let b64 = base64::engine::general_purpose::STANDARD.encode(image_url.as_bytes());
        format!(
            "{}{}",
            self.proxy_base_url.as_ref().unwrap(),
            urlencoding::encode(&b64)
        )
    }

    async fn get_or_upload_watermarked_photo_file_id(
        &self,
        bot: &Bot,
        item: &BingImageResult,
    ) -> Option<String> {
        let cache_chat_id = self.cache_chat_id?;

        {
            let st = self.state.lock().unwrap();
            if let Some(fid) = st.watermark_file_ids.get(&item.id) {
                if !fid.is_empty() {
                    return Some(fid.clone());
                }
            }
        }

        let _permit = self.watermark_upload_lock.acquire().await.ok()?;

        {
            let st = self.state.lock().unwrap();
            if let Some(fid) = st.watermark_file_ids.get(&item.id) {
                if !fid.is_empty() {
                    return Some(fid.clone());
                }
            }
        }

        let response = self
            .http_client
            .get(&item.url)
            .header("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")
            .header("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8")
            .send()
            .await
            .ok()?;

        if !response.status().is_success() {
            crate::log!(
                "Watermark cache download failed: {} {}",
                response.status().as_u16(),
                item.url
            );
            return None;
        }

        let content_type = response
            .headers()
            .get(reqwest::header::CONTENT_TYPE)
            .and_then(|v| v.to_str().ok())
            .map(|s| s.split(';').next().unwrap_or(s).trim().to_string())
            .unwrap_or_else(|| "application/octet-stream".to_string());

        if !content_type.to_lowercase().starts_with("image/") {
            crate::log!(
                "Watermark cache skipped non-image content-type '{}': {}",
                content_type,
                item.url
            );
            return None;
        }

        let bytes = response.bytes().await.ok()?.to_vec();
        let watermark_text = {
            let st = self.state.lock().unwrap();
            st.watermark_text.clone()
        };
        let result = self.watermark_service.apply_watermark_or_original(
            &bytes,
            &content_type,
            &watermark_text,
        );
        if !result.is_watermarked {
            crate::log!("Watermark cache skipped unsupported image: {}", item.url);
            return None;
        }

        let input =
            teloxide::types::InputFile::memory(result.bytes).file_name(format!("{}.jpg", item.id));
        let sent = bot
            .send_photo(ChatId(cache_chat_id), input)
            .disable_notification(true)
            .await
            .ok()?;

        let file_id = sent.photo().and_then(|photos| {
            photos
                .iter()
                .max_by_key(|p| p.width as u32 * p.height as u32)
                .map(|p| p.file.id.clone())
        });

        let file_id = match file_id {
            Some(f) if !f.is_empty() => f,
            _ => {
                crate::log!(
                    "Watermark cache upload did not return photo file_id: {}",
                    item.url
                );
                return None;
            }
        };

        {
            let mut st = self.state.lock().unwrap();
            st.watermark_file_ids
                .insert(item.id.clone(), file_id.clone());
            st.save();
        }
        Some(file_id)
    }
}

fn source_markup(item: &BingImageResult) -> teloxide::types::InlineKeyboardMarkup {
    let (text, url) = crate::helpers::telegram_ui_helper::source_button(item);
    teloxide::types::InlineKeyboardMarkup::new(vec![vec![
        teloxide::types::InlineKeyboardButton::url(text, url.parse().unwrap()),
    ]])
}

#[allow(unused_imports)]
use escape_markdown_v2 as _esc;
