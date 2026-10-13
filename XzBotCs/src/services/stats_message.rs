use crate::helpers::telegram_ui_helper;
use crate::models::bot_state::SharedState;
use crate::services::bot_stats::BotStatsService;
use crate::services::search_provider_registry::SearchProviderRegistry;
use std::sync::Arc;
use teloxide::prelude::*;
use teloxide::types::{ChatId, InputFile, InputMedia, InputMediaPhoto, ParseMode};

/// Аналог Services/StatsMessageService.cs.
pub struct StatsMessageService {
    search_registry: Arc<SearchProviderRegistry>,
    stats_service: Arc<BotStatsService>,
    state: SharedState,
}

impl StatsMessageService {
    pub fn new(
        search_registry: Arc<SearchProviderRegistry>,
        stats_service: Arc<BotStatsService>,
        state: SharedState,
    ) -> Self {
        StatsMessageService {
            search_registry,
            stats_service,
            state,
        }
    }

    fn build_markup(&self) -> teloxide::types::InlineKeyboardMarkup {
        // ВАЖНО: не держим state.lock() при вызове active_key() — он сам берёт этот мьютекс (дедлок).
        let wm = {
            let st = self.state.lock().unwrap();
            st.is_watermark_enabled
        };
        let provider = self.search_registry.active_key();
        telegram_ui_helper::build_stats_markup(wm, &provider)
    }

    pub async fn send_stats(&self, bot: &Bot, chat_id: ChatId) -> ResponseResult<()> {
        let provider_statuses = self.search_registry.check_all().await;
        let text = self.stats_service.build_stats_text(&provider_statuses);
        let markup = self.build_markup();

        let chart_bytes = self.stats_service.generate_chart_image();
        if !chart_bytes.is_empty() {
            let input = InputFile::memory(chart_bytes).file_name("stats.png");
            let r = bot
                .send_photo(chat_id, input)
                .caption(text.clone())
                .parse_mode(ParseMode::MarkdownV2)
                .reply_markup(markup.clone())
                .await;
            if let Err(e) = r {
                crate::log!("send_stats photo failed: {}, retry as text", e);
                let _ = bot
                    .send_message(chat_id, text)
                    .parse_mode(ParseMode::MarkdownV2)
                    .reply_markup(markup)
                    .await;
            }
        } else {
            let r = bot
                .send_message(chat_id, text.clone())
                .parse_mode(ParseMode::MarkdownV2)
                .reply_markup(markup.clone())
                .await;
            if let Err(e) = r {
                crate::log!("send_stats text failed: {}, retry plain", e);
                let _ = bot.send_message(chat_id, text).reply_markup(markup).await;
            }
        }
        Ok(())
    }

    pub async fn refresh_stats(
        &self,
        bot: &Bot,
        chat_id: ChatId,
        message_id: i32,
    ) -> ResponseResult<()> {
        let provider_statuses = self.search_registry.check_all().await;
        let text = self.stats_service.build_stats_text(&provider_statuses);
        let markup = self.build_markup();
        let msg_id = teloxide::types::MessageId(message_id);

        let chart_bytes = self.stats_service.generate_chart_image();
        if !chart_bytes.is_empty() {
            let input = InputFile::memory(chart_bytes).file_name("stats.png");
            let media = InputMedia::Photo(InputMediaPhoto::new(input));
            let _ = bot.edit_message_media(chat_id, msg_id, media).await;
            let _ = bot
                .edit_message_caption(chat_id, msg_id)
                .caption(text)
                .parse_mode(ParseMode::MarkdownV2)
                .reply_markup(markup)
                .await;
        } else {
            let r = bot
                .edit_message_text(chat_id, msg_id, text.clone())
                .parse_mode(ParseMode::MarkdownV2)
                .reply_markup(markup.clone())
                .await;
            if r.is_err() {
                let _ = bot
                    .edit_message_caption(chat_id, msg_id)
                    .caption(text)
                    .parse_mode(ParseMode::MarkdownV2)
                    .reply_markup(markup)
                    .await;
            }
        }
        Ok(())
    }
}
