import httpx

from aiogram import F
from aiogram.filters import Command
from aiogram.types import CallbackQuery, InlineKeyboardButton, InlineKeyboardMarkup, Message
from aiogram.utils.keyboard import InlineKeyboardBuilder

from xz.config import get_admin_id
from xz.stats import build_metrics_text, build_stats_text, get_metrics, get_stats


async def check_bing() -> tuple[bool, str]:
    try:
        async with httpx.AsyncClient(timeout=3.0, follow_redirects=True) as client:
            response = await client.get("https://www.bing.com")
            return response.status_code < 400, str(response.status_code)
    except Exception as exc:
        return False, type(exc).__name__


def _stats_keyboard() -> InlineKeyboardMarkup:
    builder = InlineKeyboardBuilder()
    builder.row(
        InlineKeyboardButton(text="📈 Метрики", callback_data="stats:metrics"),
        InlineKeyboardButton(text="🔄 Обновить", callback_data="stats:refresh"),
    )
    return builder.as_markup()


def _metrics_keyboard() -> InlineKeyboardMarkup:
    builder = InlineKeyboardBuilder()
    builder.row(
        InlineKeyboardButton(text="◀️ Назад", callback_data="stats:back"),
        InlineKeyboardButton(text="🔄 Обновить", callback_data="stats:metrics"),
    )
    return builder.as_markup()


def register_stats_handler(router) -> None:
    @router.message(Command("stats"))
    async def cmd_stats(message: Message):
        admin_id = get_admin_id()
        if not message.from_user or message.from_user.id != admin_id:
            await message.answer("⛔ Нет доступа", parse_mode="MarkdownV2")
            return

        stats = get_stats()
        bing_ok, bing_status = await check_bing()
        text = build_stats_text(stats, bing_ok, bing_status)
        await message.answer(text, parse_mode="MarkdownV2", reply_markup=_stats_keyboard())

    @router.callback_query(F.data == "stats:refresh")
    async def callback_refresh(callback: CallbackQuery):
        admin_id = get_admin_id()
        if not callback.from_user or callback.from_user.id != admin_id:
            await callback.answer("⛔ Нет доступа", show_alert=True)
            return

        stats = get_stats()
        bing_ok, bing_status = await check_bing()
        text = build_stats_text(stats, bing_ok, bing_status)
        try:
            await callback.message.edit_text(
                text, parse_mode="MarkdownV2", reply_markup=_stats_keyboard()
            )
        except Exception:
            pass  # Текст не изменился — Telegram вернёт ошибку, игнорируем
        await callback.answer("Обновлено ✅")

    @router.callback_query(F.data == "stats:metrics")
    async def callback_metrics(callback: CallbackQuery):
        admin_id = get_admin_id()
        if not callback.from_user or callback.from_user.id != admin_id:
            await callback.answer("⛔ Нет доступа", show_alert=True)
            return

        metrics = get_metrics()
        text = build_metrics_text(metrics)
        try:
            await callback.message.edit_text(
                text, parse_mode="MarkdownV2", reply_markup=_metrics_keyboard()
            )
        except Exception:
            pass
        await callback.answer()

    @router.callback_query(F.data == "stats:back")
    async def callback_back(callback: CallbackQuery):
        admin_id = get_admin_id()
        if not callback.from_user or callback.from_user.id != admin_id:
            await callback.answer("⛔ Нет доступа", show_alert=True)
            return

        stats = get_stats()
        bing_ok, bing_status = await check_bing()
        text = build_stats_text(stats, bing_ok, bing_status)
        try:
            await callback.message.edit_text(
                text, parse_mode="MarkdownV2", reply_markup=_stats_keyboard()
            )
        except Exception:
            pass
        await callback.answer()
