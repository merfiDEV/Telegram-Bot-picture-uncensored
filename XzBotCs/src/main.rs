use std::collections::HashSet;
use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex};
use teloxide::prelude::*;
use teloxide::types::UpdateKind;
use xzbot::app_config::AppConfig;
use xzbot::commands::broadcast::BroadcastService;
use xzbot::handlers::callback_query::CallbackQueryHandler;
use xzbot::handlers::inline_query::InlineQueryService;
use xzbot::handlers::message_command::MessageCommandHandler;

use xzbot::models::bot_state::BotState;
use xzbot::models::pref_store::PrefStore;
use xzbot::proxy::proxy_server::ProxyServer;
use xzbot::services::bot_stats::BotStatsService;
use xzbot::services::search_provider_registry::{RoutingSearchService, SearchProviderRegistry};
use xzbot::services::stats_message::StatsMessageService;
use xzbot::services::watermark::WatermarkService;

#[tokio::main]
async fn main() {
    let config = match AppConfig::load() {
        Ok(c) => c,
        Err(e) => {
            xzbot::log!("Error: {}", e);
            return;
        }
    };

    xzbot::log_writer::init_logging();

    let state = Arc::new(Mutex::new(BotState::load()));
    let prefs = Arc::new(Mutex::new(PrefStore::load()));

    // admin ids = env admins + extra admins from state
    let mut admin_ids: HashSet<i64> = config.admin_ids.clone();
    {
        let st = state.lock().unwrap();
        for a in &st.extra_admins {
            admin_ids.insert(*a);
        }
    }
    let admin_ids = Arc::new(admin_ids);

    let cache_chat_id = config.cache_chat_id;

    let http_client = reqwest::Client::builder().build().unwrap();

    let search_registry = Arc::new(SearchProviderRegistry::new(state.clone()));

    let watermark_service = Arc::new(WatermarkService::new());
    let stats_service = Arc::new(BotStatsService::new(state.clone()));
    let stats_message_service = Arc::new(StatsMessageService::new(
        search_registry.clone(),
        stats_service.clone(),
        state.clone(),
    ));

    let routing_service = Arc::new(RoutingSearchService::with_registry(search_registry.clone()));
    let broadcast_service = Arc::new(BroadcastService::new(
        state.clone(),
        routing_service.clone(),
        stats_service.clone(),
        http_client.clone(),
    ));

    let callback_handler = Arc::new(CallbackQueryHandler::new(
        state.clone(),
        search_registry.clone(),
        stats_service.clone(),
        stats_message_service.clone(),
        admin_ids.clone(),
    ));

    let message_handler = Arc::new(MessageCommandHandler::new(
        state.clone(),
        prefs.clone(),
        admin_ids.clone(),
        stats_service.clone(),
        broadcast_service.clone(),
        stats_message_service.clone(),
        search_registry.clone(),
    ));

    let proxy_listener_started = Arc::new(AtomicBool::new(false));
    let inline_service = Arc::new(InlineQueryService::new(
        state.clone(),
        admin_ids.clone(),
        stats_service.clone(),
        routing_service.clone(),
        search_registry.clone(),
        watermark_service.clone(),
        http_client.clone(),
        Arc::new(tokio::sync::Semaphore::new(3)),
        cache_chat_id,
        config.proxy_base_url.clone(),
        proxy_listener_started.clone(),
    ));

    let proxy_server = Arc::new(ProxyServer::new(
        http_client.clone(),
        watermark_service.clone(),
        state.clone(),
    ));

    let bot = Bot::new(config.bot_token.clone());

    // Проверяем соединение и получаем username.
    let me = match bot.get_me().await {
        Ok(me) => me,
        Err(e) => {
            xzbot::log!("Error getting bot info: {}", e);
            return;
        }
    };
    {
        let mut u = inline_service.bot_username.lock().unwrap();
        *u = Some(me.username().to_string());
    }
    xzbot::log!("Start listening for @{}", me.username());
    match &config.proxy_base_url {
        Some(url) if !url.is_empty() => xzbot::log!("Watermark proxy URL: {}", url),
        _ => xzbot::log!("Watermark proxy URL is not configured. Set PROXY_BASE_URL, for example: https://example.com/img?u="),
    }
    xzbot::log!("Bot is running. Press Ctrl+C to exit.");

    // Graceful shutdown.
    let (ct_tx, ct_rx) = tokio::sync::watch::channel(false);
    let shutdown_state = state.clone();

    // Запуск прокси.
    {
        let proxy = proxy_server.clone();
        let rx = ct_rx.clone();
        let port = config.proxy_port;
        tokio::spawn(async move {
            proxy.start(port, rx).await;
        });
    }

    // Ctrl+C.
    {
        let tx = ct_tx.clone();
        tokio::spawn(async move {
            let _ = tokio::signal::ctrl_c().await;
            xzbot::log!("Shutdown requested, stopping...");
            let _ = tx.send(true);
        });
    }

    let mut rx = ct_rx.clone();

    let handler = dptree::entry()
        .branch(Update::filter_message().endpoint({
            let h = message_handler.clone();
            move |bot: Bot, msg: teloxide::types::Message| {
                let h = h.clone();
                async move {
                    tokio::spawn(async move {
                        if let Some(text) = msg.text() {
                            xzbot::log!("MSG from {}: '{}'", msg.chat.id.0, text);
                            match h.handle(&bot, msg.clone(), text).await {
                                Ok(handled) => xzbot::log!("MSG handled={}", handled),
                                Err(e) => xzbot::log!("Message handler error: {}", e),
                            }
                        } else {
                            xzbot::log!("MSG from {} (non-text)", msg.chat.id.0);
                        }
                    });
                    respond(())
                }
            }
        }))
        .branch(Update::filter_callback_query().endpoint({
            let h = callback_handler.clone();
            move |bot: Bot, q: teloxide::types::CallbackQuery| {
                let h = h.clone();
                async move {
                    tokio::spawn(async move {
                        xzbot::log!(
                            "CALLBACK from {}: '{}'",
                            q.from.id.0,
                            q.data.clone().unwrap_or_default()
                        );
                        if let Err(e) = h.handle(&bot, q).await {
                            xzbot::log!("callback error: {}", e);
                        }
                    });
                    respond(())
                }
            }
        }))
        .branch(Update::filter_inline_query().endpoint({
            let h = inline_service.clone();
            move |bot: Bot, q: teloxide::types::InlineQuery| {
                let h = h.clone();
                async move {
                    tokio::spawn(async move {
                        xzbot::log!("INLINE from {}: '{}'", q.from.id.0, q.query);
                        if let Err(e) = h.handle(&bot, q).await {
                            xzbot::log!("inline error: {}", e);
                        }
                    });
                    respond(())
                }
            }
        }));

    let mut dispatcher = Dispatcher::builder(bot, handler)
        .enable_ctrlc_handler()
        .build();

    tokio::select! {
        _ = dispatcher.dispatch() => {}
        _ = rx.changed() => {
            // shutdown
        }
    }

    {
        let st = shutdown_state.lock().unwrap();
        st.save();
    }
    xzbot::log!("State saved. Bye.");
}

#[allow(unused_imports)]
use UpdateKind as _uk;
