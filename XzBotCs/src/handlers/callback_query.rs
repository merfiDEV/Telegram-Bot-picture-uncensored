use crate::helpers::telegram_ui_helper;
use crate::models::bot_state::SharedState;
use crate::services::bot_stats::BotStatsService;
use crate::services::search_provider_registry::SearchProviderRegistry;
use crate::services::stats_message::StatsMessageService;
use std::collections::HashSet;
use std::sync::Arc;
use teloxide::prelude::*;
use teloxide::types::{
    CallbackQuery, ChatId, InlineKeyboardButton, InlineKeyboardMarkup, MessageId, ParseMode,
};

pub const DASH_PAGE: &str = "dash:page:";
pub const DASH_FILTER: &str = "dash:filter:";
pub const DASH_SEARCH: &str = "dash:search";
pub const DASH_REFRESH: &str = "dash:refresh";

/// Аналог Handlers/CallbackQueryHandler.cs.
pub struct CallbackQueryHandler {
    state: SharedState,
    search_registry: Arc<SearchProviderRegistry>,
    stats_service: Arc<BotStatsService>,
    stats_message_service: Arc<StatsMessageService>,
    admin_ids: Arc<HashSet<i64>>,
}

impl CallbackQueryHandler {
    pub fn new(
        state: SharedState,
        search_registry: Arc<SearchProviderRegistry>,
        stats_service: Arc<BotStatsService>,
        stats_message_service: Arc<StatsMessageService>,
        admin_ids: Arc<HashSet<i64>>,
    ) -> Self {
        CallbackQueryHandler {
            state,
            search_registry,
            stats_service,
            stats_message_service,
            admin_ids,
        }
    }

    pub async fn handle(&self, bot: &Bot, q: CallbackQuery) -> ResponseResult<()> {
        let from_id = q.from.id.0 as i64;
        if self.admin_ids.is_empty() || !self.admin_ids.contains(&from_id) {
            let _ = bot
                .answer_callback_query(q.id.clone())
                .text("⛔ Нет доступа")
                .show_alert(true)
                .await;
            return Ok(());
        }

        let data = q.data.clone().unwrap_or_default();
        let msg = match q.regular_message() {
            Some(m) => m,
            None => return Ok(()),
        };
        let chat_id = msg.chat.id;
        let message_id = msg.id;

        if data == "toggle_wm" {
            let enabled = {
                let mut st = self.state.lock().unwrap();
                st.is_watermark_enabled = !st.is_watermark_enabled;
                st.save();
                st.is_watermark_enabled
            };
            let _ = bot
                .answer_callback_query(q.id.clone())
                .text(format!(
                    "Ватермарка: {}",
                    if enabled { "ВКЛ" } else { "ВЫКЛ" }
                ))
                .await;
            let _ = self
                .stats_message_service
                .refresh_stats(bot, chat_id, message_id.0)
                .await;
        } else if data == "toggle_provider" {
            let next = self.search_registry.fallback_key();
            if let Some(next_key) = next {
                if self.search_registry.set_active(&next_key) {
                    let _ = bot
                        .answer_callback_query(q.id.clone())
                        .text(format!(
                            "Провайдер: {}",
                            self.search_registry.active_key().to_uppercase()
                        ))
                        .await;
                } else {
                    let _ = bot
                        .answer_callback_query(q.id.clone())
                        .text("Не удалось переключить")
                        .show_alert(true)
                        .await;
                }
            }
            let _ = self
                .stats_message_service
                .refresh_stats(bot, chat_id, message_id.0)
                .await;
        } else if data == "stats:refresh" {
            let _ = self
                .stats_message_service
                .refresh_stats(bot, chat_id, message_id.0)
                .await;
            let _ = bot
                .answer_callback_query(q.id.clone())
                .text("Обновлено ✅")
                .await;
        } else if data == "stats:back" {
            let _ = self
                .stats_message_service
                .refresh_stats(bot, chat_id, message_id.0)
                .await;
            let _ = bot.answer_callback_query(q.id.clone()).await;
        } else if data == "stats:metrics" {
            let text = self.stats_service.build_metrics_text();
            let markup = InlineKeyboardMarkup::new(vec![
                vec![InlineKeyboardButton::callback("◀️ Назад", "stats:back")],
                vec![InlineKeyboardButton::callback(
                    "🔄 Обновить",
                    "stats:metrics",
                )],
            ]);
            let r = bot
                .edit_message_text(chat_id, message_id, text.clone())
                .parse_mode(ParseMode::MarkdownV2)
                .reply_markup(markup.clone())
                .await;
            if r.is_err() {
                let _ = bot
                    .edit_message_caption(chat_id, message_id)
                    .caption(text)
                    .parse_mode(ParseMode::MarkdownV2)
                    .reply_markup(markup)
                    .await;
            }
            let _ = bot.answer_callback_query(q.id.clone()).await;
        } else if data == "stats:dashboard" {
            let text =
                self.stats_service
                    .build_dashboard_text(q.from.id.0 as i64, None, None, None);
            self.show_dashboard(bot, chat_id, message_id, q.from.id.0 as i64, text)
                .await;
            let _ = bot.answer_callback_query(q.id.clone()).await;
        } else if let Some(rest) = data.strip_prefix(DASH_PAGE) {
            if let Ok(new_page) = rest.parse::<i32>() {
                let text = self.stats_service.build_dashboard_text(
                    q.from.id.0 as i64,
                    Some(new_page),
                    None,
                    None,
                );
                self.show_dashboard(bot, chat_id, message_id, q.from.id.0 as i64, text)
                    .await;
            }
            let _ = bot.answer_callback_query(q.id.clone()).await;
        } else if let Some(filter) = data.strip_prefix(DASH_FILTER) {
            let text = self.stats_service.build_dashboard_text(
                q.from.id.0 as i64,
                Some(0),
                Some(filter),
                None,
            );
            self.show_dashboard(bot, chat_id, message_id, q.from.id.0 as i64, text)
                .await;
            let _ = bot.answer_callback_query(q.id.clone()).await;
        } else if data == DASH_REFRESH {
            let text =
                self.stats_service
                    .build_dashboard_text(q.from.id.0 as i64, None, None, None);
            self.show_dashboard(bot, chat_id, message_id, q.from.id.0 as i64, text)
                .await;
            let _ = bot
                .answer_callback_query(q.id.clone())
                .text("Обновлено ✅")
                .await;
        } else if data == "dash:search:clear" {
            let text = self.stats_service.build_dashboard_text(
                q.from.id.0 as i64,
                Some(0),
                None,
                Some(""),
            );
            self.show_dashboard(bot, chat_id, message_id, q.from.id.0 as i64, text)
                .await;
            let _ = bot
                .answer_callback_query(q.id.clone())
                .text("Поиск сброшен")
                .await;
        } else if data == DASH_SEARCH {
            self.stats_service
                .set_dashboard_awaiting_search(q.from.id.0 as i64, true);
            let _ = bot
                .answer_callback_query(q.id.clone())
                .text("Отправьте текст для поиска в ЛС бота")
                .show_alert(true)
                .await;
        }

        Ok(())
    }

    async fn show_dashboard(
        &self,
        bot: &Bot,
        chat_id: ChatId,
        message_id: MessageId,
        user_id: i64,
        text: String,
    ) {
        let markup = self.build_dashboard_markup(user_id);
        let r = bot
            .edit_message_text(chat_id, message_id, text.clone())
            .parse_mode(ParseMode::MarkdownV2)
            .reply_markup(markup.clone())
            .await;
        if r.is_err() {
            let _ = bot
                .edit_message_caption(chat_id, message_id)
                .caption(text.clone())
                .parse_mode(ParseMode::MarkdownV2)
                .reply_markup(markup.clone())
                .await;
        }
    }

    fn build_dashboard_markup(&self, user_id: i64) -> InlineKeyboardMarkup {
        let ds = self.stats_service.dashboard_state(user_id);
        let mut buttons: Vec<Vec<InlineKeyboardButton>> = Vec::new();

        buttons.push(vec![
            InlineKeyboardButton::callback("◀️ Назад", format!("dash:page:{}", ds.page - 1)),
            InlineKeyboardButton::callback(
                format!("{}", ds.page + 1),
                format!("dash:page:{}", ds.page),
            ),
            InlineKeyboardButton::callback("Вперед ▶️", format!("dash:page:{}", ds.page + 1)),
        ]);

        let all_label = if ds.filter == "all" {
            "✅ Все"
        } else {
            "Все"
        };
        let success_label = if ds.filter == "success" {
            "✅ Успешные"
        } else {
            "Успешные"
        };
        let errors_label = if ds.filter == "errors" {
            "✅ Ошибки"
        } else {
            "Ошибки"
        };
        buttons.push(vec![
            InlineKeyboardButton::callback(all_label, "dash:filter:all"),
            InlineKeyboardButton::callback(success_label, "dash:filter:success"),
            InlineKeyboardButton::callback(errors_label, "dash:filter:errors"),
        ]);

        let mut extra = vec![InlineKeyboardButton::callback(
            "🔄 Обновить",
            "dash:refresh",
        )];
        if !ds.search.is_empty() {
            extra.push(InlineKeyboardButton::callback(
                "🔍 Сброс поиска",
                "dash:search:clear",
            ));
        } else {
            extra.push(InlineKeyboardButton::callback("🔍 Поиск", "dash:search"));
        }
        buttons.push(extra);
        buttons.push(vec![InlineKeyboardButton::callback(
            "◀️ Назад",
            "stats:back",
        )]);

        InlineKeyboardMarkup::new(buttons)
    }
}

#[allow(unused_imports)]
use telegram_ui_helper as _tuh;
