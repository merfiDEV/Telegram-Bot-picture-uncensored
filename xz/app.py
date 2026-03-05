import asyncio

from aiogram import Bot, Dispatcher

from xz.config import get_bot_token
from xz.handlers.inline import register_inline_handler
from xz.handlers.start import register_start_handler
from xz.logging_setup import setup_logging


def create_dispatcher() -> Dispatcher:
    dp = Dispatcher()
    register_start_handler(dp)
    register_inline_handler(dp)
    return dp


async def main() -> None:
    setup_logging()
    bot = Bot(token=get_bot_token())
    dp = create_dispatcher()
    await bot.delete_webhook(drop_pending_updates=True)
    await dp.start_polling(bot)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("Бот выключен")
