import time
from collections import defaultdict
from datetime import datetime


_START_TIME = time.monotonic()
_STARTED_AT = datetime.now()
_USAGE_COUNT = 0
_ERROR_COUNT = 0
_response_times: list[float] = []
_error_details: dict[str, int] = defaultdict(int)


def increment_usage() -> None:
    global _USAGE_COUNT
    _USAGE_COUNT += 1


def increment_error() -> None:
    global _ERROR_COUNT
    _ERROR_COUNT += 1


def record_request_time(seconds: float) -> None:
    _response_times.append(seconds)


def record_error(error_type: str) -> None:
    _error_details[error_type] += 1
    increment_error()


def get_stats() -> dict:
    uptime_seconds = max(0, int(time.monotonic() - _START_TIME))
    return {
        "uptime_seconds": uptime_seconds,
        "started_at": _STARTED_AT,
        "usage_count": _USAGE_COUNT,
        "error_count": _ERROR_COUNT,
    }


def get_metrics() -> dict:
    uptime_seconds = max(0, int(time.monotonic() - _START_TIME))
    requests_per_min = 0.0
    if uptime_seconds > 0:
        requests_per_min = round(_USAGE_COUNT / (uptime_seconds / 60), 2)

    if _response_times:
        times_ms = [t * 1000 for t in _response_times]
        avg_time = round(sum(times_ms) / len(times_ms), 1)
        min_time = round(min(times_ms), 1)
        max_time = round(max(times_ms), 1)
    else:
        avg_time = min_time = max_time = None

    return {
        "usage_count": _USAGE_COUNT,
        "error_count": _ERROR_COUNT,
        "error_details": dict(_error_details),
        "requests_per_min": requests_per_min,
        "avg_response_ms": avg_time,
        "min_response_ms": min_time,
        "max_response_ms": max_time,
        "total_requests_measured": len(_response_times),
    }


def format_metrics(metrics: dict) -> str:
    lines = ["*📈 Метрики бота*", "---"]

    # Response time
    if metrics["avg_response_ms"] is not None:
        lines.append(
            f"*⏱ Отклик Bing:* "
            f"среднее `{metrics['avg_response_ms']}мс` | "
            f"мин `{metrics['min_response_ms']}мс` | "
            f"макс `{metrics['max_response_ms']}мс`"
        )
        lines.append(f"*📊 Замеров:* `{metrics['total_requests_measured']}`")
    else:
        lines.append("*⏱ Отклик Bing:* нет данных")

    lines.append("---")

    # Error details
    error_details = metrics["error_details"]
    if error_details:
        lines.append("*⚠️ Ошибки по типам:*")
        for err_type, count in sorted(error_details.items(), key=lambda x: -x[1]):
            lines.append(f"  `{err_type}` — `{count}`")
    else:
        lines.append("*⚠️ Ошибки:* отсутствуют")

    lines.append("---")

    # Request stats
    lines.append(
        f"*🔢 Запросы:* `{metrics['usage_count']}` "
        f"(`{metrics['requests_per_min']}` запросов/мин)"
    )
    lines.append("---")

    return "\n".join(lines)


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
