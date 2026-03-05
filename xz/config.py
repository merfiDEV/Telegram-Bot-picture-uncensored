import os

from dotenv import load_dotenv


def get_bot_token() -> str:
    load_dotenv()
    token = os.getenv("BOT_TOKEN", "").strip()
    if not token:
        raise RuntimeError("BOT_TOKEN is not set")
    return token
