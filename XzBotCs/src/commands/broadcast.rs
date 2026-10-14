use crate::helpers::telegram_escaper::escape_markdown_v2;
use crate::models::bot_state::SharedState;
use crate::services::bot_stats::BotStatsService;
use crate::services::search_provider_registry::RoutingSearchService;
use std::sync::Arc;
use teloxide::prelude::*;
use teloxide::types::{ChatAction, ChatId, InputFile, ParseMode};

/// Аналог Commands/BroadcastService.cs.
pub struct BroadcastService {
    state: SharedState,
    search_service: Arc<RoutingSearchService>,
    stats_service: Arc<BotStatsService>,
    http_client: reqwest::Client,
}

impl BroadcastService {
    pub fn new(
        state: SharedState,
        search_service: Arc<RoutingSearchService>,
        stats_service: Arc<BotStatsService>,
        http_client: reqwest::Client,
    ) -> Self {
        BroadcastService {
            state,
            search_service,
            stats_service,
            http_client,
        }
    }

    pub async fn send_random_image(
        &self,
        bot: &Bot,
        chat_id: ChatId,
        topic: &str,
    ) -> ResponseResult<()> {
        // Чёрный список: отклоняем запрещённые темы.
        {
            let bl = {
                let st = self.state.lock().unwrap();
                st.blacklist.clone()
            };
            if let Some(word) = crate::models::bot_state::find_blacklisted(topic, &bl) {
                crate::log!("BLOCKED /random topic '{}' (matched '{}')", topic, word);
                self.stats_service.record_error("blacklisted");
                bot.send_message(chat_id, "🚫 Такой запрос запрещён.")
                    .await?;
                return Ok(());
            }
        }

        let _ = bot.send_chat_action(chat_id, ChatAction::UploadPhoto).await;

        let wm_enabled = {
            let st = self.state.lock().unwrap();
            st.is_watermark_enabled
        };

        let limit = if wm_enabled { 6 } else { 30 };
        let response = self
            .search_service
            .search_images_detailed(topic, 1, limit)
            .await;
        let items = response.items;
        if items.is_empty() {
            bot.send_message(
                chat_id,
                "😕 Ничего не нашлось по запросу\\. Попробуй другую тему\\.",
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
            return Ok(());
        }

        self.stats_service
            .record_request(0, None, &format!("{} --random", topic), true);

        let mut shuffled = items;
        use rand::seq::SliceRandom;
        shuffled.shuffle(&mut rand::thread_rng());

        let mut sent = false;
        for pick in shuffled {
            let caption = format!("🎲 *{}*", escape_markdown_v2(topic));
            // Доминирующие 👍/👎 (с числом голосов) + «Перейти на сайт» внизу. Тег = тема запроса.
            let (up, down) = {
                let st = self.state.lock().unwrap();
                st.tag_ratings
                    .get(topic)
                    .map(|r| (r.up, r.down))
                    .unwrap_or((0, 0))
            };
            let markup = crate::helpers::telegram_ui_helper::photo_markup_with_rating(
                &pick, topic, up, down,
            );

            let url_parsed = match pick.url.parse() {
                Ok(u) => u,
                Err(_) => continue,
            };

            let send_result = if pick.is_gif {
                bot.send_animation(chat_id, InputFile::url(url_parsed))
                    .caption(caption.clone())
                    .parse_mode(ParseMode::MarkdownV2)
                    .reply_markup(markup.clone())
                    .await
            } else {
                bot.send_photo(chat_id, InputFile::url(url_parsed))
                    .caption(caption.clone())
                    .parse_mode(ParseMode::MarkdownV2)
                    .reply_markup(markup.clone())
                    .await
            };

            if let Ok(msg) = &send_result {
                // Кэшируем file_id для мгновенной повторной выдачи в inline.
                if let Some(photos) = msg.photo() {
                    if let Some(best) = photos
                        .iter()
                        .max_by_key(|p| p.width as u32 * p.height as u32)
                    {
                        let mut st = self.state.lock().unwrap();
                        st.preview_cache
                            .insert(pick.id.clone(), best.file.id.clone());
                        st.save();
                    }
                }
                sent = true;
                break;
            }

            if let Ok(resp) = self.http_client.get(&pick.url).send().await {
                if resp.status().is_success() {
                    if let Ok(bytes) = resp.bytes().await {
                        let filename = if pick.is_gif {
                            "animation.gif"
                        } else {
                            "photo.jpg"
                        };
                        let input = InputFile::memory(bytes.to_vec()).file_name(filename);
                        let r2 = if pick.is_gif {
                            bot.send_animation(chat_id, input)
                                .caption(caption)
                                .parse_mode(ParseMode::MarkdownV2)
                                .reply_markup(markup)
                                .await
                        } else {
                            bot.send_photo(chat_id, input)
                                .caption(caption)
                                .parse_mode(ParseMode::MarkdownV2)
                                .reply_markup(markup)
                                .await
                        };
                        if r2.is_ok() {
                            sent = true;
                            break;
                        }
                    }
                }
            }
        }

        if !sent {
            bot.send_message(chat_id, "😕 Не удалось загрузить ни одно изображение по вашему запросу\\. Попробуйте другую тему\\.")
                .parse_mode(ParseMode::MarkdownV2)
                .await?;
        }

        Ok(())
    }

    pub async fn send_broadcast(
        &self,
        bot: &Bot,
        admin_chat_id: ChatId,
        announcement: &str,
    ) -> ResponseResult<()> {
        let subscribers: Vec<i64> = {
            let st = self.state.lock().unwrap();
            st.subscribers.iter().copied().collect()
        };

        if subscribers.is_empty() {
            bot.send_message(admin_chat_id, "📭 Список подписчиков пуст.")
                .await?;
            return Ok(());
        }

        bot.send_message(
            admin_chat_id,
            format!(
                "🚀 Начинаю рассылку для {} подписчиков\\.\\.\\.",
                subscribers.len()
            ),
        )
        .parse_mode(ParseMode::MarkdownV2)
        .await?;

        let mut success = 0;
        let mut failed = 0;
        let mut failed_ids = Vec::new();

        for user_id in subscribers {
            let result = bot
                .send_message(
                    ChatId(user_id),
                    format!(
                        "📢 *Обновление*

{}",
                        announcement
                    ),
                )
                .parse_mode(ParseMode::Markdown)
                .await;
            match result {
                Ok(_) => success += 1,
                Err(e) => {
                    failed += 1;
                    let is_blocked = matches!(
                        e,
                        teloxide::RequestError::Api(teloxide::ApiError::BotBlocked)
                    );
                    if is_blocked {
                        let mut st = self.state.lock().unwrap();
                        st.subscribers.remove(&user_id);
                        failed_ids.push(user_id);
                    }
                    crate::log!("Broadcast: failed to send to {}: {}", user_id, e);
                }
            }
            tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        }

        if !failed_ids.is_empty() {
            let st = self.state.lock().unwrap();
            st.save();
        }

        bot.send_message(
            admin_chat_id,
            format!(
                "✅ Рассылка завершена\\.

📨 Отправлено: {}
❌ Не удалось: {}",
                success, failed
            ),
        )
        .parse_mode(ParseMode::MarkdownV2)
        .await?;

        Ok(())
    }
}
