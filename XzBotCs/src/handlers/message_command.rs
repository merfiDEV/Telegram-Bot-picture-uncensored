use crate::app_config::DEVELOPER_PROFILE_URL;
use crate::commands::broadcast::BroadcastService;
use crate::helpers::telegram_escaper::escape_markdown_v2;
use crate::helpers::user_input_helper::extract_user_id;
use crate::models::bot_state::SharedState;
use crate::models::pref_store::PrefStore;
use crate::services::bot_stats::BotStatsService;
use crate::services::search_provider_registry::SearchProviderRegistry;
use crate::services::stats_message::StatsMessageService;
use std::collections::HashSet;
use std::sync::Arc;
use teloxide::prelude::*;
use teloxide::types::{ChatId, InlineKeyboardButton, InlineKeyboardMarkup, Message, ParseMode};

/// Аналог Handlers/MessageCommandHandler.cs.
pub struct MessageCommandHandler {
    state: SharedState,
    prefs: std::sync::Arc<std::sync::Mutex<PrefStore>>,
    admin_ids: Arc<HashSet<i64>>,
    stats_service: Arc<BotStatsService>,
    broadcast_service: Arc<BroadcastService>,
    stats_message_service: Arc<StatsMessageService>,
    search_registry: Arc<SearchProviderRegistry>,
}

impl MessageCommandHandler {
    pub fn new(
        state: SharedState,
        prefs: std::sync::Arc<std::sync::Mutex<PrefStore>>,
        admin_ids: Arc<HashSet<i64>>,
        stats_service: Arc<BotStatsService>,
        broadcast_service: Arc<BroadcastService>,
        stats_message_service: Arc<StatsMessageService>,
        search_registry: Arc<SearchProviderRegistry>,
    ) -> Self {
        MessageCommandHandler {
            state,
            prefs,
            admin_ids,
            stats_service,
            broadcast_service,
            stats_message_service,
            search_registry,
        }
    }

    fn is_admin(&self, user_id: Option<i64>) -> bool {
        match user_id {
            Some(id) => self.admin_ids.is_empty() || self.admin_ids.contains(&id),
            None => false,
        }
    }

    pub async fn handle(
        &self,
        bot: &Bot,
        message: Message,
        message_text: &str,
    ) -> ResponseResult<bool> {
        let from_id = message.from.as_ref().map(|u| u.id.0 as i64);

        if matches!(message.chat.kind, teloxide::types::ChatKind::Private(_)) {
            if let Some(id) = from_id {
                let added = {
                    let mut st = self.state.lock().unwrap();
                    st.subscribers.insert(id)
                };
                if added {
                    let st = self.state.lock().unwrap();
                    st.save();
                }
            }
        }

        // Обработка ввода поиска дашборда
        if matches!(message.chat.kind, teloxide::types::ChatKind::Private(_))
            && from_id.is_some()
            && !message_text.starts_with('/')
        {
            let awaiting = {
                let st = self.state.lock().unwrap();
                st.dashboard_states
                    .get(&from_id.unwrap())
                    .map(|d| d.awaiting_search)
                    .unwrap_or(false)
            };
            if awaiting {
                {
                    let mut st = self.state.lock().unwrap();
                    if let Some(d) = st.dashboard_states.get_mut(&from_id.unwrap()) {
                        d.awaiting_search = false;
                    }
                }
                let dash_text = self.stats_service.build_dashboard_text(
                    from_id.unwrap(),
                    Some(0),
                    None,
                    Some(message_text.trim()),
                );
                let r = bot
                    .send_message(message.chat.id, dash_text.clone())
                    .parse_mode(ParseMode::MarkdownV2)
                    .await;
                if r.is_err() {
                    let _ = bot.send_message(message.chat.id, dash_text).await;
                }
                return Ok(true);
            }
        }

        if message_text.starts_with("/start") {
            self.handle_start(bot, &message, message_text).await?;
        } else if message_text == "/help" {
            self.handle_help(bot, &message).await?;
        } else if message_text == "/stats" {
            crate::log!(
                "STATS cmd from {:?}, is_admin={}",
                from_id,
                self.is_admin(from_id)
            );
            if !self.is_admin(from_id) {
                bot.send_message(message.chat.id, "⛔ Нет доступа")
                    .parse_mode(ParseMode::MarkdownV2)
                    .await?;
                return Ok(true);
            }
            self.stats_message_service
                .send_stats(bot, message.chat.id)
                .await?;
        } else if message_text == "/logs" {
            self.handle_logs(bot, &message, from_id).await?;
        } else if message_text.starts_with("/setwatermark") {
            self.handle_set_watermark(bot, &message, message_text, from_id)
                .await?;
        } else if message_text.starts_with("/makeadmin") {
            self.handle_make_admin(bot, &message, message_text, from_id)
                .await?;
        } else if message_text.starts_with("/unmakeadmin") {
            self.handle_unmake_admin(bot, &message, message_text, from_id)
                .await?;
        } else if message_text.starts_with("/setpref") {
            self.handle_set_pref(bot, &message, message_text, from_id)
                .await?;
        } else if message_text.starts_with("/provider") {
            self.handle_provider(bot, &message, message_text, from_id)
                .await?;
        } else if message_text.starts_with("/admins") {
            self.handle_admins(bot, &message, from_id).await?;
        } else if message_text.starts_with("/random") {
            self.handle_random(bot, &message, message_text).await?;
        } else if message_text.starts_with("/upt") {
            self.handle_upt(bot, &message, message_text, from_id)
                .await?;
        } else {
            return Ok(false);
        }

        Ok(true)
    }

    async fn handle_start(
        &self,
        bot: &Bot,
        message: &Message,
        message_text: &str,
    ) -> ResponseResult<()> {
        if message_text.contains("register")
            && matches!(message.chat.kind, teloxide::types::ChatKind::Private(_))
        {
            if let Some(u) = &message.from {
                let mut st = self.state.lock().unwrap();
                st.subscribers.insert(u.id.0 as i64);
                st.save();
            }
            let markup = InlineKeyboardMarkup::new(vec![vec![
                InlineKeyboardButton::switch_inline_query_current_chat(
                    "🔍 Начать использовать",
                    "",
                ),
            ]]);
            bot.send_message(
                message.chat.id,
                "✅ *Регистрация подтверждена\\!*

Теперь вы можете пользоваться ботом в inline\\-режиме в любом чате\\.",
            )
            .parse_mode(ParseMode::MarkdownV2)
            .reply_markup(markup)
            .await?;
            return Ok(());
        }

        if message_text.contains("developer") {
            let markup = InlineKeyboardMarkup::new(vec![vec![InlineKeyboardButton::url(
                "💻 Открыть профиль",
                DEVELOPER_PROFILE_URL.parse().unwrap(),
            )]]);
            bot.send_message(message.chat.id, "💻 *Профиль разработчика*")
                .parse_mode(ParseMode::MarkdownV2)
                .reply_markup(markup)
                .await?;
            return Ok(());
        }

        let text = "*🤖 Бот работает в асинхронном inline режиме!*

Чтобы использовать бота, откройте любой чат и введите:
`@имя_бота ваш_запрос`

⚡ Используйте флаг `--gif` для анимаций и `--random` для случайной картинки.
🎲 Команда `/random тема` — «мне повезёт»: одна случайная картинка по теме.

⚠️ *Дисклеймер*
Данный бот автоматически обрабатывает поисковые запросы пользователей и показывает результаты из *открытых источников* в интернете.

*Важные правила:*
— Создатель не хранит и не модерирует контент
— Вся ответственность за запросы лежит на пользователе
— Используя бота, вы подтверждаете соблюдение законов вашей страны";

        let markup = InlineKeyboardMarkup::new(vec![vec![
            InlineKeyboardButton::switch_inline_query_current_chat("🔍 Попробовать поиск", ""),
        ]]);
        bot.send_message(message.chat.id, text)
            .parse_mode(ParseMode::MarkdownV2)
            .reply_markup(markup)
            .await?;
        Ok(())
    }

    async fn handle_help(&self, bot: &Bot, message: &Message) -> ResponseResult<()> {
        let help_text = "*📖 Справка*

Бот ищет картинки через inline\\-режим\\. Введите `@имя_бота запрос` в любом чате\\.

*Флаги поиска:*
— `--gif` — искать анимации
— `--random` — одна случайная картинка по теме

*Команды:*
— `/random тема` — «мне повезёт»: случайная картинка
— `/start` — приветствие
— `/help` — эта справка

Примеры:
`@имя_бота котики`
`@имя_бота dance --gif`
`@имя_бота cyberpunk --random`
`/random лес`";
        bot.send_message(message.chat.id, help_text)
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
        Ok(())
    }

    async fn handle_logs(
        &self,
        bot: &Bot,
        message: &Message,
        from_id: Option<i64>,
    ) -> ResponseResult<()> {
        if !matches!(message.chat.kind, teloxide::types::ChatKind::Private(_)) {
            bot.send_message(message.chat.id, "Доступно только в ЛС")
                .await?;
            return Ok(());
        }
        if !self.is_admin(from_id) {
            bot.send_message(message.chat.id, "Нет доступа :/").await?;
            return Ok(());
        }

        let logs_path = std::path::Path::new("..").join("logs");
        let _ = std::fs::create_dir_all(&logs_path);

        let mut log_files: Vec<std::path::PathBuf> = std::fs::read_dir(&logs_path)
            .map(|rd| {
                rd.filter_map(|e| e.ok())
                    .map(|e| e.path())
                    .filter(|p| {
                        p.file_name()
                            .and_then(|n| n.to_str())
                            .map(|n| n.starts_with("logs_") && n.ends_with(".txt"))
                            .unwrap_or(false)
                    })
                    .collect()
            })
            .unwrap_or_default();

        log_files.sort_by_key(|p| std::fs::metadata(p).and_then(|m| m.modified()).ok());
        log_files.reverse();
        log_files.truncate(5);

        let zip_path = std::env::temp_dir().join(format!("logs_{}.zip", uuid_like()));
        let zip_result = (|| -> std::io::Result<()> {
            use std::io::Write;
            let file = std::fs::File::create(&zip_path)?;
            let mut zip = zip::ZipWriter::new(file);
            let options = zip::write::SimpleFileOptions::default();
            for lf in &log_files {
                if let Some(name) = lf.file_name().and_then(|n| n.to_str()) {
                    zip.start_file(name, options)?;
                    if let Ok(data) = std::fs::read(lf) {
                        zip.write_all(&data)?;
                    }
                }
            }
            zip.finish()?;
            Ok(())
        })();

        match zip_result {
            Ok(_) => {
                if let Ok(data) = std::fs::read(&zip_path) {
                    let input = teloxide::types::InputFile::memory(data).file_name("logs.zip");
                    let _ = bot.send_document(message.chat.id, input).await;
                }
            }
            Err(e) => {
                bot.send_message(
                    message.chat.id,
                    format!(
                        "Ошибка при сборе логов: {}",
                        escape_markdown_v2(&e.to_string())
                    ),
                )
                .parse_mode(ParseMode::MarkdownV2)
                .await?;
            }
        }

        if zip_path.exists() {
            let _ = std::fs::remove_file(&zip_path);
        }
        Ok(())
    }

    fn strip_command_prefix(&self, message_text: &str, base: &str) -> String {
        let prefix = if message_text.starts_with(&format!("{}@", base)) {
            match message_text.find(' ') {
                Some(i) => &message_text[..i],
                None => message_text,
            }
        } else {
            base
        };
        message_text[prefix.len()..].trim().to_string()
    }

    async fn handle_set_watermark(
        &self,
        bot: &Bot,
        message: &Message,
        message_text: &str,
        from_id: Option<i64>,
    ) -> ResponseResult<()> {
        if !self.is_admin(from_id) {
            bot.send_message(message.chat.id, "⛔ Нет доступа")
                .parse_mode(ParseMode::MarkdownV2)
                .await?;
            return Ok(());
        }
        let new_watermark = self.strip_command_prefix(message_text, "/setwatermark");
        if new_watermark.is_empty() {
            bot.send_message(
                message.chat.id,
                "⚠️ Укажите новый текст водяного знака\\.
Пример: `/setwatermark Новый Текст`",
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
            return Ok(());
        }
        {
            let mut st = self.state.lock().unwrap();
            st.watermark_text = new_watermark.clone();
            st.watermark_file_ids.clear();
            st.save();
        }
        bot.send_message(
            message.chat.id,
            format!(
                "✅ Текст водяного знака изменен на: `{}`\\.
Кэш старых ватермарок в Telegram очищен\\.",
                escape_markdown_v2(&new_watermark)
            ),
        )
        .parse_mode(ParseMode::MarkdownV2)
        .await?;
        Ok(())
    }

    async fn handle_make_admin(
        &self,
        bot: &Bot,
        message: &Message,
        message_text: &str,
        from_id: Option<i64>,
    ) -> ResponseResult<()> {
        if !self.is_admin(from_id) {
            bot.send_message(message.chat.id, "⛔ Нет доступа")
                .parse_mode(ParseMode::MarkdownV2)
                .await?;
            return Ok(());
        }
        let arg = self.strip_command_prefix(message_text, "/makeadmin");
        let new_admin_id = extract_user_id(&arg);
        if new_admin_id == 0 {
            bot.send_message(
                message.chat.id,
                "⚠️ Укажите корректный ID пользователя\\.
Примеры:
`/makeadmin 1741079861`
`/makeadmin tg://user?id=1741079861`",
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
            return Ok(());
        }
        let added = {
            let mut st = self.state.lock().unwrap();
            st.extra_admins.insert(new_admin_id);
            st.save();
            new_admin_id
        };
        let _ = added;
        let admin_text = format!(
            "✦ ────────────── ✦
👑 *Администратор выдан*

🆔 ID: `{}`

{}
✦ ────────────── ✦",
            new_admin_id, "✅ Пользователь получил полный доступ к админ\\-командам\\."
        );
        bot.send_message(message.chat.id, admin_text)
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
        Ok(())
    }

    async fn handle_unmake_admin(
        &self,
        bot: &Bot,
        message: &Message,
        message_text: &str,
        from_id: Option<i64>,
    ) -> ResponseResult<()> {
        if !self.is_admin(from_id) {
            bot.send_message(message.chat.id, "⛔ Нет доступа")
                .parse_mode(ParseMode::MarkdownV2)
                .await?;
            return Ok(());
        }
        let arg = self.strip_command_prefix(message_text, "/unmakeadmin");
        let remove_admin_id = extract_user_id(&arg);
        if remove_admin_id == 0 {
            bot.send_message(
                message.chat.id,
                "⚠️ Укажите корректный ID пользователя\\.
Примеры:
`/unmakeadmin 1741079861`
`/unmakeadmin tg://user?id=1741079861`",
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
            return Ok(());
        }
        {
            let mut st = self.state.lock().unwrap();
            st.extra_admins.remove(&remove_admin_id);
            st.save();
        }
        let admin_text = format!(
            "✦ ────────────── ✦
🚫 *Администратор снят*

🆔 ID: `{}`

{}
✦ ────────────── ✦",
            remove_admin_id, "❌ Пользователь лишён админ\\-доступа\\."
        );
        bot.send_message(message.chat.id, admin_text)
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
        Ok(())
    }

    async fn handle_set_pref(
        &self,
        bot: &Bot,
        message: &Message,
        message_text: &str,
        from_id: Option<i64>,
    ) -> ResponseResult<()> {
        if !self.is_admin(from_id) {
            bot.send_message(message.chat.id, "⛔ Нет доступа")
                .parse_mode(ParseMode::MarkdownV2)
                .await?;
            return Ok(());
        }
        let arg = self.strip_command_prefix(message_text, "/setpref");
        if arg.is_empty() {
            bot.send_message(
                message.chat.id,
                "⚠️ Укажите ID и префикс\\.
Пример: `/setpref 1741079861 🅰`
Для очистки: `/setpref 1741079861`",
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
            return Ok(());
        }
        let parts: Vec<&str> = arg.splitn(2, |c| c == ' ' || c == '\t').collect();
        let target_id = extract_user_id(parts[0]);
        if target_id == 0 {
            bot.send_message(
                message.chat.id,
                "⚠️ Неверный ID\\. Пример: `/setpref 1741079861 🅰`",
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
            return Ok(());
        }
        let prefix = if parts.len() > 1 {
            parts[1].trim().to_string()
        } else {
            String::new()
        };
        {
            let mut prefs = self.prefs.lock().unwrap();
            if prefix.is_empty() {
                prefs.prefixes.remove(&target_id);
            } else {
                prefs.prefixes.insert(target_id, prefix.clone());
            }
            prefs.save();
        }
        let prefix_text = format!(
            "*— Статус изменён —*
ID: `{}`
{}",
            target_id,
            if prefix.is_empty() {
                "Префикс снят\\.".to_string()
            } else {
                format!("Префикс: {}", escape_markdown_v2(&prefix))
            }
        );
        bot.send_message(message.chat.id, prefix_text)
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
        Ok(())
    }

    async fn handle_provider(
        &self,
        bot: &Bot,
        message: &Message,
        message_text: &str,
        from_id: Option<i64>,
    ) -> ResponseResult<()> {
        if !self.is_admin(from_id) {
            bot.send_message(message.chat.id, "⛔ Нет доступа")
                .parse_mode(ParseMode::MarkdownV2)
                .await?;
            return Ok(());
        }
        let first_space = message_text.find(|c| c == ' ' || c == '\t');
        let arg = match first_space {
            Some(i) => message_text[i + 1..].trim().to_lowercase(),
            None => String::new(),
        };

        if arg.is_empty() {
            let available = self
                .search_registry
                .all_keys()
                .iter()
                .map(|(k, d)| format!("{} ({})", k, d))
                .collect::<Vec<_>>()
                .join(", ");
            bot.send_message(
                message.chat.id,
                format!("🔎 *Провайдер поиска*\nАктивный: `{}`\nДоступные: {}\n\nИспользование: `/provider bing` или `/provider ddg`",
                    escape_markdown_v2(&self.search_registry.active_key()),
                    escape_markdown_v2(&available)),
            ).parse_mode(ParseMode::MarkdownV2).await?;
            return Ok(());
        }

        if self.search_registry.set_active(&arg) {
            bot.send_message(
                message.chat.id,
                format!(
                    "✅ Провайдер переключён на `{}`",
                    escape_markdown_v2(&self.search_registry.active_key())
                ),
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
        } else {
            bot.send_message(
                message.chat.id,
                format!("⚠️ Неизвестный провайдер: `{}`", escape_markdown_v2(&arg)),
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
        }
        Ok(())
    }

    async fn handle_admins(
        &self,
        bot: &Bot,
        message: &Message,
        from_id: Option<i64>,
    ) -> ResponseResult<()> {
        if !self.is_admin(from_id) {
            bot.send_message(message.chat.id, "⛔ Нет доступа")
                .parse_mode(ParseMode::MarkdownV2)
                .await?;
            return Ok(());
        }
        let mut ids: Vec<i64> = self.admin_ids.iter().copied().collect();
        ids.sort();
        let mut lines = vec!["*Администрация*".to_string()];
        for admin_id in ids {
            let username = match bot.get_chat(ChatId(admin_id)).await {
                Ok(c) => c.username().map(|s| s.to_string()),
                Err(_) => None,
            };
            let display = match username {
                Some(u) if !u.is_empty() => format!("@{}", u),
                _ => "без username".to_string(),
            };
            let pref = {
                let prefs = self.prefs.lock().unwrap();
                prefs.prefixes.get(&admin_id).cloned()
            };
            let prefix = match pref {
                Some(p) if !p.is_empty() => format!("{} — ", escape_markdown_v2(&p)),
                _ => String::new(),
            };
            lines.push(format!(
                "{}{} — `{}`",
                prefix,
                escape_markdown_v2(&display),
                admin_id
            ));
        }
        bot.send_message(
            message.chat.id,
            lines.join(
                "
",
            ),
        )
        .parse_mode(ParseMode::MarkdownV2)
        .await?;
        Ok(())
    }

    async fn handle_random(
        &self,
        bot: &Bot,
        message: &Message,
        message_text: &str,
    ) -> ResponseResult<()> {
        let topic = self.strip_command_prefix(message_text, "/random");
        if topic.is_empty() {
            bot.send_message(
                message.chat.id,
                "🎲 Укажите тему\\.
Пример: `/random кот`",
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
            return Ok(());
        }
        self.broadcast_service
            .send_random_image(bot, message.chat.id, &topic)
            .await?;
        Ok(())
    }

    async fn handle_upt(
        &self,
        bot: &Bot,
        message: &Message,
        message_text: &str,
        from_id: Option<i64>,
    ) -> ResponseResult<()> {
        if !self.is_admin(from_id) {
            bot.send_message(message.chat.id, "⛔ Нет доступа")
                .parse_mode(ParseMode::MarkdownV2)
                .await?;
            return Ok(());
        }
        let announcement = self.strip_command_prefix(message_text, "/upt");
        if announcement.is_empty() {
            bot.send_message(
                message.chat.id,
                "⚠️ Укажите текст рассылки\\.
Пример: `/upt Важное обновление!`",
            )
            .parse_mode(ParseMode::MarkdownV2)
            .await?;
            return Ok(());
        }
        self.broadcast_service
            .send_broadcast(bot, message.chat.id, &announcement)
            .await?;
        Ok(())
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
