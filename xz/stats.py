import time
from datetime import datetime


_START_TIME = time.monotonic()
_STARTED_AT = datetime.now()
_USAGE_COUNT = 0
_ERROR_COUNT = 0


def increment_usage() -> None:
    global _USAGE_COUNT
    _USAGE_COUNT += 1


def increment_error() -> None:
    global _ERROR_COUNT
    _ERROR_COUNT += 1


def get_stats() -> dict:
    uptime_seconds = max(0, int(time.monotonic() - _START_TIME))
    return {
        "uptime_seconds": uptime_seconds,
        "started_at": _STARTED_AT,
        "usage_count": _USAGE_COUNT,
        "error_count": _ERROR_COUNT,
    }


def format_uptime(seconds: int) -> str:
    days, remainder = divmod(seconds, 86400)
    hours, remainder = divmod(remainder, 3600)
    minutes, secs = divmod(remainder, 60)

    parts = []
    if days:
        parts.append(f"{days}д")
    if hours:
        parts.append(f"{hours}ч")
    if minutes:
        parts.append(f"{minutes}м")
    parts.append(f"{secs}с")

    return " ".join(parts)


def format_started_at(value: datetime) -> str:
    return value.strftime("%Y-%m-%d %H:%M:%S")
