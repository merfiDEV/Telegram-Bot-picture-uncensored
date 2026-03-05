from aiogram.filters import Command
from aiogram.types import Message

from xz.config import get_admin_id
from xz.stats import format_uptime, get_stats


def register_stats_handler(router) -> None:
    @router.message(Command("stats"))
    async def cmd_stats(message: Message):
        admin_id = get_admin_id()
        if not message.from_user or message.from_user.id != admin_id:
            await message.answer("Нет доступа", parse_mode="Markdown")
            return

        stats = get_stats()
        uptime = format_uptime(stats["uptime_seconds"])

        text = (
            "*📊 Статистика бота*\n\n"
            f"*⏱ Время работы:* `{uptime}`\n"
            f"*⚠️ Ошибок:* `{stats['error_count']}`\n"
        )
        await message.answer(text, parse_mode="Markdown")
