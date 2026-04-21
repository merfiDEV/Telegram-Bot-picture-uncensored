<div align="center">

<img src="https://raw.githubusercontent.com/tandpfun/skill-icons/main/icons/Telegram.svg" width="80" />

# XZ Inline Image Bot

**Мгновенный поиск изображений прямо в Telegram — в любом чате, без команд.**

[![Python](https://img.shields.io/badge/Python-3.10+-3670A0?style=flat-square&logo=python&logoColor=ffdd54)](https://www.python.org/)
[![aiogram](https://img.shields.io/badge/aiogram-3.x-2CA5E0?style=flat-square&logo=telegram&logoColor=white)](https://github.com/aiogram/aiogram)
[![CI](https://img.shields.io/github/actions/workflow/status/merfiDEV/Telegram-Bot-picture-uncensored/python-app.yml?style=flat-square&label=tests)](https://github.com/merfiDEV/Telegram-Bot-picture-uncensored/actions)
[![License: MIT](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)

[🚀 Попробовать бота](http://t.me/Velikiarbyzz_bot) · [📖 Документация](#-быстрый-запуск) · [🐛 Сообщить об ошибке](https://github.com/merfiDEV/Telegram-Bot-picture-uncensored/issues)

</div>

---

## О проекте

**XZ Image Bot** — асинхронный Telegram-бот для поиска изображений в inline-режиме. Введи запрос прямо в строке сообщения любого чата и моментально получи результаты поиска через Bing.

```
@Velikiarbyzz_bot закат на море
@Velikiarbyzz_bot funny cat --gif
```

## Возможности

| Функция | Описание |
|---|---|
| 🔍 **Inline-поиск** | Ищи картинки прямо в любом чате без переходов в бота |
| 🎬 **Поиск GIF** | Добавь флаг `--gif` для поиска анимаций |
| 🌐 **Ссылка на источник** | Кнопка под фото ведёт на сайт-источник изображения |
| ⚡ **Асинхронность** | Параллельная проверка всех ссылок, минимальная задержка |
| ♾️ **Пагинация** | Листай результаты вниз — бот подгрузит новые |

## Стек технологий

- **[aiogram 3.x](https://github.com/aiogram/aiogram)** — асинхронный фреймворк для Telegram Bot API
- **[httpx](https://github.com/encode/httpx)** — HTTP-клиент с поддержкой async/await
- **Bing Image Search** — движок поиска с доступом к миллиардам изображений
- **pytest + GitHub Actions** — автоматическое тестирование при каждом пуше

## Быстрый запуск

**1. Клонируй репозиторий:**

```bash
git clone https://github.com/merfiDEV/Telegram-Bot-picture-uncensored.git
cd Telegram-Bot-picture-uncensored
```

**2. Создай виртуальное окружение и установи зависимости:**

```bash
python -m venv venv

# Linux / macOS
source venv/bin/activate

# Windows
.\venv\Scripts\activate

pip install -r requirements.txt
```

**3. Настрой `.env`:**

```env
BOT_TOKEN=твой_токен_от_BotFather
ADMIN_ID=твой_telegram_id
```

**4. Запусти бота:**

```bash
python -m xz.app
```

## Запуск тестов

```bash
python -m pytest
```

Тесты также запускаются автоматически через GitHub Actions при каждом `git push` в ветку `main`.

## Структура проекта

```
.
├── .github/
│   └── workflows/
│       └── python-app.yml   # CI pipeline
├── tests/
│   └── test_bing_images.py  # Unit & integration tests
├── xz/
│   ├── handlers/
│   │   ├── inline.py        # Обработчик inline-запросов
│   │   ├── start.py         # Команда /start
│   │   └── stats.py         # Статистика
│   ├── services/
│   │   └── bing_images.py   # Поиск и парсинг изображений
│   ├── app.py               # Точка входа
│   ├── config.py            # Конфигурация
│   └── stats.py             # Сбор статистики
├── .env                     # Переменные окружения (не коммитить!)
├── requirements.txt
└── README.md
```

## Использование

Открой любой чат и начни вводить запрос через имя бота:

```
@Velikiarbyzz_bot котики
```

Для поиска GIF:

```
@Velikiarbyzz_bot котики --gif
```

## Отказ от ответственности

Бот является инструментом поиска по открытым источникам. Ответственность за использование контента лежит на конечном пользователе. Уважайте авторское право и проверяйте лицензии изображений.

---

<div align="center">
  Сделано с ❤️ by <a href="https://github.com/merfiDEV">merfiDEV</a>
</div>
