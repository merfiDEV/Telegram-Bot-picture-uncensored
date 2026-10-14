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

        // Ratings are available to ALL users (not only admins) — handle before the permission check.
        let data0 = q.data.clone().unwrap_or_default();
        if let Some(rest) = data0.strip_prefix("rate:") {
            return self.handle_rating(bot, q, rest).await;
        }

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

    /// Обработка голоса за тег: rest = "up:<tagEnc>" или "down:<tagEnc>".
    async fn handle_rating(&self, bot: &Bot, q: CallbackQuery, rest: &str) -> ResponseResult<()> {
        let (dir, tag_enc) = match rest.split_once(':') {
            Some((d, t)) => (d, t),
            None => return Ok(()),
        };
        use base64::Engine;
        let tag = match base64::engine::general_purpose::URL_SAFE_NO_PAD.decode(tag_enc) {
            Ok(bytes) => String::from_utf8(bytes).unwrap_or_default(),
            Err(_) => return Ok(()),
        };
        if tag.is_empty() {
            return Ok(());
        }

        let user_id = q.from.id.0 as i64;
        let want: i8 = if dir == "up" { 1 } else { -1 };

        // Один пользователь = один голос на тег: повторное нажатие снимает голос,
        // нажатие противоположного — меняет знак.
        let (up_count, down_count, action) = {
            let mut st = self.state.lock().unwrap();
            let cur = st
                .user_votes
                .get(&tag)
                .and_then(|m| m.get(&user_id))
                .copied()
                .unwrap_or(0);
            let new = if cur == want { 0 } else { want };

            let entry = st.user_votes.entry(tag.clone()).or_default();
            if new == 0 {
                entry.remove(&user_id);
            } else {
                entry.insert(user_id, new);
            }

            let (mut u, mut d) = (0i32, 0i32);
            if let Some(votes) = st.user_votes.get(&tag) {
                for v in votes.values() {
                    if *v > 0 {
                        u += 1;
                    } else if *v < 0 {
                        d += 1;
                    }
                }
            }
            let rating = st.tag_ratings.entry(tag.clone()).or_default();
            rating.up = u;
            rating.down = d;
            st.save();
            let action = if new == 0 {
                "off"
            } else if new > 0 {
                "up"
            } else {
                "down"
            };
            (u, d, action)
        };
        crate::log!(
            "RATING {} tag='{}' user={} (up={} down={})",
            action,
            tag,
            user_id,
            up_count,
            down_count
        );

        let _ = bot
            .answer_callback_query(q.id.clone())
            .text(if action == "off" {
                "Голос снят"
            } else if action == "up" {
                "Спасибо! 👍"
            } else {
                "Спасибо! 👎"
            })
            .await;

        // Обновляем счётчики на кнопках, сохраняя остальные ряды (напр. «Перейти на сайт»).
        if let Some(msg) = q.regular_message() {
            use base64::Engine;
            let tag_enc = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(tag.as_bytes());
            let mut rows: Vec<Vec<teloxide::types::InlineKeyboardButton>> = Vec::new();
            rows.push(vec![
                teloxide::types::InlineKeyboardButton::callback(
                    format!("👍 {}", up_count),
                    format!("rate:up:{}", tag_enc),
                ),
                teloxide::types::InlineKeyboardButton::callback(
                    format!("👎 {}", down_count),
                    format!("rate:down:{}", tag_enc),
                ),
            ]);
            if let Some(existing) = msg.reply_markup() {
                for row in existing.inline_keyboard.iter().skip(1) {
                    rows.push(row.clone());
                }
            }
            let markup = teloxide::types::InlineKeyboardMarkup::new(rows);
            let _ = bot
                .edit_message_reply_markup(msg.chat.id, msg.id)
                .reply_markup(markup)
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
