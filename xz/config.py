import os

from dotenv import load_dotenv

load_dotenv()


def get_bot_token() -> str:
    token = os.getenv("BOT_TOKEN", "").strip()
    if not token:
        raise RuntimeError("BOT_TOKEN is not set")
    return token


def get_admin_id() -> int:
    raw_value = os.getenv("ADMIN_ID", "").strip()
    if not raw_value:
        raise RuntimeError("ADMIN_ID is not set")
    try:
        return int(raw_value)
    except ValueError as exc:
        raise RuntimeError("ADMIN_ID must be an integer") from exc
