from aiogram.filters import Command, CommandObject
from aiogram.types import InlineKeyboardButton, Message
from aiogram.utils.keyboard import InlineKeyboardBuilder


DEVELOPER_PROFILE_URL = "https://t.me/Tyta_Zdesyaa777"


def register_start_handler(router) -> None:
    @router.message(Command("start"))
    async def cmd_start(message: Message, command: CommandObject):
        if command.args == "developer":
            builder = InlineKeyboardBuilder()
            builder.row(
                InlineKeyboardButton(
                    text="\U0001f4bb \u041e\u0442\u043a\u0440\u044b\u0442\u044c \u043f\u0440\u043e\u0444\u0438\u043b\u044c",
                    url=DEVELOPER_PROFILE_URL,
                )
            )
            await message.answer(
                "\U0001f4bb *\u041f\u0440\u043e\u0444\u0438\u043b\u044c \u0440\u0430\u0437\u0440\u0430\u0431\u043e\u0442\u0447\u0438\u043a\u0430*",
                parse_mode="Markdown",
                reply_markup=builder.as_markup(),
            )
            return

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
            "— Используя бота, вы подтверждаете соблюдение законов вашей страны"
        )
        
        builder = InlineKeyboardBuilder()
        builder.row(InlineKeyboardButton(text="🔍 Попробовать поиск", switch_inline_query_current_chat=""))
        
        await message.answer(
            text, 
            parse_mode="Markdown",
            reply_markup=builder.as_markup()
        )
