from aiogram import types
from aiogram.filters import Command
from aiogram.types import Message, InlineKeyboardMarkup, InlineKeyboardButton
from aiogram.utils.keyboard import InlineKeyboardBuilder


def register_start_handler(router) -> None:
    @router.message(Command("start"))
    async def cmd_start(message: Message):
        text = (
            "*🤖 Бот работает в асинхронном inline режиме!*\n\n"
            "Чтобы использовать бота, откройте любой чат и введите:\n"
            "`@имя_бота ваш_запрос`\n\n"
            "⚡ *Новинка:* Используйте флаг `--gif` в конце запроса для поиска анимаций.\n\n"
            "⚠️ *Дисклеймер*\n"
            "Данный бот автоматически обрабатывает поисковые запросы пользователей и "
            "показывает результаты из *открытых источников* в интернете.\n\n"
            "*Важные правила:*\n"
            "— Создатель не хранит и не модерирует контент\n"
            "— Вся ответственность за запросы лежит на пользователе\n"
            "— Используя бота, вы подтверждаете соблюдение законов вашей страны\n\n"
            "💎 *Премиум-функции:* Кнопка «Открыть оригинал» под каждым изображением для доступа к высокому качеству."
        )
        
        builder = InlineKeyboardBuilder()
        builder.row(InlineKeyboardButton(text="🔍 Попробовать поиск", switch_inline_query_current_chat=""))
        
        await message.answer(
            text, 
            parse_mode="Markdown",
            reply_markup=builder.as_markup()
        )
