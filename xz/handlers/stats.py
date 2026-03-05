import httpx

from aiogram.filters import Command
from aiogram.types import Message

from xz.config import get_admin_id
from xz.stats import format_started_at, format_uptime, get_stats


async def check_bing() -> tuple[bool, str]:
    try:
        async with httpx.AsyncClient(timeout=3.0, follow_redirects=True) as client:
            response = await client.get("https://www.bing.com")
            return response.status_code < 400, str(response.status_code)
    except Exception as exc:
        return False, type(exc).__name__


def register_stats_handler(router) -> None:
    @router.message(Command("stats"))
    async def cmd_stats(message: Message):
        admin_id = get_admin_id()
        if not message.from_user or message.from_user.id != admin_id:
            await message.answer("Нет доступа", parse_mode="Markdown")
            return

        stats = get_stats()
        uptime = format_uptime(stats["uptime_seconds"])
        started_at = format_started_at(stats["started_at"])
        bing_ok, bing_status = await check_bing()
        bing_mark = "✅" if bing_ok else "❌"

        text = (
            "*📊 Статистика бота*\n"
            "---\n"
            f"*⏱ Время работы:* `{uptime}`\n"
            f"*🗓 Дата запуска:* `{started_at}`\n"
            "---\n"
            f"*🌐 Bing:* {bing_mark} ` {bing_status} `\n"
            f"*⚠️ Ошибок:* `{stats['error_count']}`\n"
            "---\n"
            "[[ admin only ]]"
        )
        await message.answer(text, parse_mode="Markdown")
