import time
from collections import defaultdict
from datetime import datetime


from collections import deque

_START_TIME = time.monotonic()
_STARTED_AT = datetime.now()
_USAGE_COUNT = 0
_ERROR_COUNT = 0
_response_times: list[float] = []
_error_details: dict[str, int] = defaultdict(int)
_recent_requests = deque(maxlen=20)


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


def record_request(user_id: int, username: str, query: str, success: bool) -> None:
    _recent_requests.appendleft({
        "time": datetime.now(),
        "user_id": user_id,
        "username": username or "Unknown",
        "query": query,
        "success": success
    })


def get_recent_requests() -> list[dict]:
    return list(_recent_requests)


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


def _esc(text: str) -> str:
    """Escape special chars for MarkdownV2."""
    special = r"\_*[]()~`>#+=|{}.!-"
    return "".join(f"\\{c}" if c in special else c for c in str(text))


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
    return value.strftime("%d.%m.%Y %H:%M:%S")


def build_stats_text(stats: dict, bing_ok: bool, bing_status: str) -> str:
    uptime = format_uptime(stats["uptime_seconds"])
    started_at = format_started_at(stats["started_at"])
    bing_icon = "✅" if bing_ok else "❌"

    error_count = stats["error_count"]
    usage_count = stats["usage_count"]

    success_count = max(0, usage_count - error_count)
    success_rate = (
        round(success_count / usage_count * 100, 1) if usage_count > 0 else 100.0
    )

    lines = [
        "📊 *Статистика бота*",
        "",
        "⏱ *Аптайм*",
        f"  `{_esc(uptime)}` \\(с `{_esc(started_at)}`\\)",
        "",
        "🌐 *Внешние сервисы*",
        f"  Bing: {bing_icon} `{_esc(bing_status)}`",
        "",
        "📈 *Запросы*",
        f"  Всего: `{_esc(str(usage_count))}`",
        f"  Успешных: `{_esc(str(success_count))}` \\({_esc(str(success_rate))}%\\)",
        f"  Ошибок: `{_esc(str(error_count))}`",
        "",
        "🔐 _admin only_",
    ]
    return "\n".join(lines)


def build_metrics_text(metrics: dict) -> str:
    lines = ["📈 *Метрики производительности*", ""]

    # Response times
    if metrics["avg_response_ms"] is not None:
        lines.append("⏱ *Время ответа Bing*")
        lines.append(f"  среднее:  `{_esc(str(metrics['avg_response_ms']))} мс`")
        lines.append(f"  мин:      `{_esc(str(metrics['min_response_ms']))} мс`")
        lines.append(f"  макс:     `{_esc(str(metrics['max_response_ms']))} мс`")
        lines.append(
            f"  замеров:  `{_esc(str(metrics['total_requests_measured']))}`"
        )
    else:
        lines.append("⏱ *Время ответа Bing:* нет данных")

    lines.append("")

    # RPM
    lines.append(
        f"🔢 *Нагрузка:* `{_esc(str(metrics['requests_per_min']))}` зап/мин"
    )
    lines.append("")

    # Errors
    error_details = metrics["error_details"]
    if error_details:
        lines.append("⚠️ *Ошибки по типам:*")
        for err_type, count in sorted(
            error_details.items(), key=lambda x: -x[1]
        ):
            lines.append(f"  `{_esc(err_type)}` — `{_esc(str(count))}`")
    else:
        lines.append("✅ *Ошибок не зафиксировано*")

    return "\n".join(lines)


def build_dashboard_text(requests: list[dict]) -> str:
    lines = ["📋 *Дашборд последних запросов*", ""]
    if not requests:
        lines.append("  _Пусто_")
        return "\n".join(lines)
    
    for i, req in enumerate(requests[:10]):
        time_str = req["time"].strftime("%H:%M:%S")
        status = "✅" if req["success"] else "❌"
        user = req["username"]
        uid = req["user_id"]
        q = req["query"]
        if len(q) > 20:
            q = q[:20] + "..."
        lines.append(f"`[{_esc(time_str)}]` {status} `@{_esc(user)}` \\(`{uid}`\\): _{_esc(q)}_")
    
    return "\n".join(lines)
