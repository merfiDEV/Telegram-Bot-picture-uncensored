# XZ Inline Image Bot

<div align="center">

![Telegram](https://img.shields.io/badge/Telegram-Inline%20Bot-2CA5E0?style=for-the-badge&logo=telegram&logoColor=white)
![Python](https://img.shields.io/badge/Python-3.10+-3670A0?style=for-the-badge&logo=python&logoColor=ffdd54)
![aiogram](https://img.shields.io/badge/aiogram-3.x-2CA5E0?style=for-the-badge&logo=telegram&logoColor=white)

**Быстрый inline-бот для поиска картинок и GIF прямо внутри Telegram.**  
Вводишь запрос в любом чате, выбираешь результат, отправляешь. Без лишних команд, переходов и ручного копирования ссылок.

[Открыть бота](https://t.me/Velikiarbyzz_bot) · [Профиль разработчика](https://t.me/Tyta_Zdesyaa777) · [Issues](https://github.com/merfiDEV/Telegram-Bot-picture-uncensored/issues)

</div>

---

## Что Это

**XZ Inline Image Bot** - асинхронный Telegram-бот на `aiogram 3`, который работает через inline-режим:

```text
@Velikiarbyzz_bot кот в очках
@Velikiarbyzz_bot funny cat --gif
```

Бот ищет изображения через Bing Images, проверяет найденные ссылки, убирает дубли, поддерживает пагинацию и возвращает результаты прямо в inline-выдачу Telegram.

---

## Возможности

| Возможность | Что дает |
|---|---|
| Inline-поиск | Поиск картинок прямо в любом чате через `@bot query` |
| GIF-режим | Флаг `--gif` включает поиск анимаций |
| Проверка ссылок | Бот валидирует image URL перед отправкой |
| Удаление дублей | Одинаковые картинки не засоряют выдачу |
| Пагинация | Telegram может подгружать следующие результаты |
| Кнопка источника | Под результатом есть переход на сайт-источник или оригинал |
| Плашка разработчика | В inline-выдаче есть верхняя кнопка профиля разработчика |
| Пустой запрос | Если запрос пустой, бот показывает аккуратную подсказку |
| Админ-статистика | `/stats` показывает состояние бота и Bing |
| Метрики | Среднее/минимальное/максимальное время ответа Bing |
| Дашборд запросов | Последние запросы пользователей для админа |
| Логи | `/logs` отправляет архив логов администратору |
| CI | Тесты запускаются через GitHub Actions |

---

## Быстрый Старт

### 1. Клонировать проект

```bash
git clone https://github.com/merfiDEV/Telegram-Bot-picture-uncensored.git
cd Telegram-Bot-picture-uncensored
```

### 2. Создать окружение

```bash
python -m venv venv
```

Windows:

```powershell
.\venv\Scripts\activate
```

Linux / macOS:

```bash
source venv/bin/activate
```

### 3. Установить зависимости

```bash
pip install -r requirements.txt
```

### 4. Создать `.env`

```env
BOT_TOKEN=telegram_bot_token_from_botfather
ADMIN_ID=your_telegram_id
```

`BOT_TOKEN` можно получить у [BotFather](https://t.me/BotFather).  
`ADMIN_ID` - числовой Telegram ID администратора.

### 5. Запустить

```bash
python -m xz.app
```

---

## Команды

| Команда | Доступ | Назначение |
|---|---:|---|
| `/start` | всем | Инструкция и кнопка запуска inline-поиска |
| `/start developer` | всем | Карточка со ссылкой на профиль разработчика |
| `/stats` | admin | Статистика, здоровье Bing, метрики и дашборд |
| `/logs` | admin | Скачать архив логов |

---

## Inline Использование

Открой любой чат и начни вводить:

```text
@Velikiarbyzz_bot запрос
```

Примеры:

```text
@Velikiarbyzz_bot cyberpunk city
@Velikiarbyzz_bot anime wallpaper
@Velikiarbyzz_bot кот мем --gif
```

Если после имени бота ничего не ввести, бот покажет подсказку вместо пустой выдачи.

---

## Архитектура

```text
.
├── .github/
│   └── workflows/
│       └── python-app.yml       # CI: установка зависимостей и pytest
├── tests/
│   └── test_bing_images.py      # тесты поиска и хеширования URL
├── xz/
│   ├── app.py                   # точка входа, запуск polling
│   ├── config.py                # BOT_TOKEN и ADMIN_ID из .env
│   ├── logging_setup.py         # настройка логов
│   ├── stats.py                 # статистика, метрики, форматирование
│   ├── handlers/
│   │   ├── inline.py            # inline-поиск и выдача результатов
│   │   ├── logs.py              # admin-команда /logs
│   │   ├── start.py             # /start и developer-переход
│   │   └── stats.py             # admin-команда /stats
│   └── services/
│       └── bing_images.py       # Bing Images, парсинг, проверка ссылок
├── requirements.txt
└── README.md
```

---

## Как Работает Поиск

1. Пользователь вводит inline-запрос в Telegram.
2. Бот парсит запрос и проверяет флаг `--gif`.
3. Сервис делает запрос к Bing Images.
4. Из HTML-ответа достаются `murl` и `purl`.
5. Каждая ссылка проверяется через `HEAD`, а при необходимости через `GET`.
6. Дубликаты отсекаются по хешу очищенного URL.
7. Telegram получает список `InlineQueryResultPhoto` или `InlineQueryResultGif`.

---

## Стек

- `Python 3.10+`
- `aiogram 3.x`
- `httpx`
- `python-dotenv`
- `pytest`
- `pytest-asyncio`
- `GitHub Actions`

---

## Тесты

```bash
python -m pytest
```

Текущие тесты проверяют:

- стабильный хеш URL без query/fragment;
- базовую интеграцию поиска Bing;
- структуру возвращаемых результатов.

---

## Админ-Мониторинг

Команда `/stats` доступна только `ADMIN_ID` и показывает:

- аптайм;
- время запуска;
- доступность Bing;
- количество запросов;
- количество ошибок;
- success rate;
- среднее, минимальное и максимальное время ответа Bing;
- последние запросы пользователей.

Команда `/logs` доступна только в личных сообщениях с ботом и отправляет архив `logs.zip`.

---

## Безопасность И Ответственность

Бот является инструментом поиска по открытым источникам. Он не хранит изображения, не владеет контентом и не гарантирует права на использование найденных материалов.

Ответственность за запросы, пересылку и дальнейшее использование результатов лежит на пользователе. Уважайте авторские права, правила Telegram и законы своей страны.

---

<div align="center">

Сделано для быстрого поиска изображений в Telegram.  
Разработчик: [@Tyta_Zdesyaa777](https://t.me/Tyta_Zdesyaa777)

</div>
