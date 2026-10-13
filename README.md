# 🖼 Telegram Bot: поиск картинок (uncensored)

Telegram-бот для поиска изображений в inline-режиме. Поддерживает двух провайдеров
(**Bing** и **DuckDuckGo**) с автоматическим фолбэком, наложение водяного знака,
HTTP-прокси для раздачи изображений и панель статистики.

> Переписан с C# (.NET 10) на **Rust**. Поведение, команды и формат данных сохранены 1:1.

## ✨ Возможности

- **Inline-поиск** изображений: `@имя_бота ваш_запрос` в любом чате
- **Два провайдера**: Bing и DuckDuckGo, переключение на лету + автофолбэк при 0 результатов
- **Флаги поиска**:
  - `--gif` — только анимированные GIF
  - `--random` — одна случайная картинка по запросу
- **Водяной знак**: наложение на изображения через Skia, кэш file_id в Telegram
- **HTTP-прокси**: `GET /img?u=<base64url>` — раздача картинок с ватермаркой
- **Статистика**: график запросов, метрики провайдеров, дашборд последних запросов
- **Админ-команды**: рассылка, управление админами, префиксы, смена ватермарки

## 🛠 Стек

| Компонент | Технология |
|-----------|-----------|
| Язык | Rust (edition 2021) |
| Telegram | [teloxide](https://github.com/teloxide/teloxide) 0.13 |
| Async runtime | tokio |
| HTTP | reqwest |
| JSON | serde / serde_json |
| Графика | skia-safe |
| Regex | regex, md-5 |

## 📦 Сборка

```bash
cd XzBotCs
cargo build --release
```

Бинарник: `XzBotCs/target/release/XzBotCs.exe`

## ⚙️ Конфигурация

Бот читает переменные окружения, а также файл `../.env` (относительно рабочей директории):

| Переменная | Описание |
|-----------|----------|
| `BOT_TOKEN` | Токен Telegram-бота (обязательно) |
| `ADMIN_ID` | ID администраторов через запятую |
| `CACHE_CHAT_ID` | Чат для кэша ватермарок (по умолчанию — первый админ) |
| `PROXY_BASE_URL` | Публичный URL прокси (напр. `https://example.com/img?u=`) |
| `PUBLIC_BASE_URL` | Альтернатива `PROXY_BASE_URL` |
| `PROXY_PORT` | Порт локального прокси (по умолчанию 8080) |

Пример `.env`:
```
BOT_TOKEN=123456:ABC-DEF...
ADMIN_ID=123456789,987654321
PROXY_BASE_URL=https://example.com/img?u=
PROXY_PORT=8080
```

## 🚀 Запуск

```bash
cd XzBotCs
./target/release/XzBotCs.exe
```

Бот создаёт лог-файлы в `../logs/logs_YYYYMMDD_HHMMSS.txt`.

## 🤖 Команды

### Пользовательские
- `/start` — приветствие
- `/help` — справка
- `/random тема` — случайная картинка по теме

### Админские
- `/stats` — статистика (график + метрики провайдеров)
- `/logs` — архив логов (zip)
- `/setwatermark текст` — сменить текст водяного знака
- `/provider [bing|ddg]` — переключить провайдера поиска
- `/makeadmin ID` — выдать админку
- `/unmakeadmin ID` — снять админку
- `/setpref ID префикс` — задать префикс пользователя
- `/admins` — список администраторов
- `/upt текст` — рассылка подписчикам

## 🔍 Inline-режим

В любом чате:
```
@имя_бота котики
@имя_бота dance --gif
@имя_бота cyberpunk --random
```

## 📊 Данные

- `bot_state.json` — состояние бота (статистика, подписчики, настройки)
- `pref.json` — префиксы пользователей

Оба файла пишутся атомарно (tmp + rename). Формат совместим с C#-версией.

## 📁 Структура

```
XzBotCs/
  Cargo.toml
  src/
    main.rs                 — точка входа, polling, логи, graceful shutdown
    app_config.rs           — конфигурация из env/.env
    log_writer.rs           — логирование (stdout + файл)
    search_types.rs         — общие типы результатов поиска
    models/                 — BotState, PrefStore (serde)
    helpers/                — escaping, парсинг ввода, UI-хелперы
    services/
      bing_search.rs        — поиск через Bing
      duckduckgo_search.rs  — поиск через DuckDuckGo
      search_provider_registry.rs — реестр провайдеров + автофолбэк
      watermark.rs          — наложение водяного знака (Skia)
      bot_stats.rs          — статистика и график
      stats_message.rs      — отправка/обновление статистики
    commands/
      broadcast.rs          — рассылка и /random
    handlers/
      inline_query.rs       — inline-запросы
      message_command.rs    — текстовые команды
      callback_query.rs     — кнопки (статистика, дашборд)
    proxy/
      proxy_server.rs       — HTTP-прокси для картинок
  tests/                    — unit-тесты
```

## 🧪 Тесты

```bash
cd XzBotCs
cargo test
```

## 📄 Лицензия

Проект распространяется как есть. Вся ответственность за использование — на пользователе.
