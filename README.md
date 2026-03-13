# XZ Inline Image Bot
<p align="left">
  <img src="https://img.shields.io/badge/python-3670A0?style=for-the-badge&logo=python&logoColor=ffdd54" />
  <img src="https://img.shields.io/badge/aiogram-3.x-2CA5E0?style=for-the-badge&logo=telegram&logoColor=white" />
  <img src="https://img.shields.io/badge/httpx-000000?style=for-the-badge&logo=scipy&logoColor=white" />
</p>

Телеграм-бот на базе aiogram, который работает в inline-режиме и возвращает результаты поиска изображений через Bing.

> 🚀 Попробовать бота: [@Velikiarbyzz_bot](http://t.me/Velikiarbyzz_bot)

## Возможности

- Inline-поиск изображений по запросу пользователя.
- Асинхронная обработка и быстрый ответ через `InlineQuery`.
- Фильтрация ссылок и проверка, что ресурс действительно изображение.
---

### ✨ О проекте
**XZ Image Bot** — это высокоскоростной Telegram-бот для мгновенного поиска изображений и анимаций. Проект создан с фокусом на **максимальное качество** и **безупречный UX**, оставаясь при этом абсолютно бесплатным для всех.

---

## 🚀 Супер-возможности
*   **🏎 Молниеносный Inline-поиск**: Ищи картинки прямо в строке ввода сообщения в любом чате.
*   **🎬 Поддержка GIF**: Используй флаг `--gif` в конце запроса, чтобы найти идеальную анимацию.
*   **🖼 Высокое качество**: Каждое изображение сопровождается кнопкой «Открыть оригинал» для доступа к первоисточнику.
*   **🔒 Приватность и безопасность**: Бот не хранит ваши данные и работает через зашифрованные API.

---

## 🛠 Технологический стек
- **Core:** [aiogram 3.x](https://github.com/aiogram/aiogram) — современный асинхронный фреймворк.
- **Engine:** Bing Image Search (Scraping) — доступ к миллиардам изображений.
- **HTTP Client:** [httpx](https://github.com/encode/httpx) — для быстрых и стабильных запросов.
- **Design:** Чистый код и премиальное форматирование сообщений.

---

## 📦 Быстрый запуск

1.  **Клонируйте репозиторий:**
    ```bash
    git clone [https://github.com/youruser/xz-image-bot.git](https://github.com/merfiDEV/Telegram-Bot-picture-uncensored)
    cd 
    ```

2.  **Настройте окружение:**
    ```bash
    python -m venv venv
    source venv/bin/activate  # Или .\venv\Scripts\activate на Windows
    pip install -r requirements.txt
    ```

3.  **Конфигурация (файл `.env`):**
    ```env
    BOT_TOKEN=твой_токен_здесь
    ADMIN_ID=твой_id
    ```

4.  **Запуск:**
    ```bash
    python -m xz.app
    ```

---

## 🎨 Как пользоваться?
Открой любой чат и введи:
> `@Velikiarbyzz_bot твой запрос` — для обычных картинок.
> `@Velikiarbyzz_bot твой запрос --gif` — для поиска гифок.

---

## ⚠️ Отказ от ответственности
Бот является инструментом поиска по открытым источникам. Ответственность за использование контента лежит на конечном пользователе. Мы уважаем авторское право и рекомендуем проверять лицензии изображений.

---

<p align="center">
  Сделано с ❤️ для тех, кто ценит качество.
</p>
