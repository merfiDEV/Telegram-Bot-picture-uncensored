import logging

from aiogram.types import (
    InlineKeyboardButton,
    InlineKeyboardMarkup,
    InlineQuery,
    InlineQueryResultArticle,
    InlineQueryResultGif,
    InlineQueryResultPhoto,
    InlineQueryResultsButton,
    InputTextMessageContent,
)

from xz.services.bing_images import search_images
from xz.stats import increment_error, increment_usage, record_request


DEVELOPER_PROFILE_BUTTON = InlineQueryResultsButton(
    text="\U0001f4bb \u041f\u0440\u043e\u0444\u0438\u043b\u044c \u0440\u0430\u0437\u0440\u0430\u0431\u043e\u0442\u0447\u0438\u043a\u0430 >",
    start_parameter="developer",
)


EMPTY_QUERY_RESULT = InlineQueryResultArticle(
    id="empty-query",
    title="\U0001f50d \u0412\u0432\u0435\u0434\u0438\u0442\u0435 \u0437\u0430\u043f\u0440\u043e\u0441",
    description="\u041d\u0430\u043f\u0438\u0448\u0438\u0442\u0435, \u043a\u0430\u043a\u0443\u044e \u043a\u0430\u0440\u0442\u0438\u043d\u043a\u0443 \u043d\u0430\u0439\u0442\u0438. \u041d\u0430\u043f\u0440\u0438\u043c\u0435\u0440: \u043a\u043e\u0442 \u0432 \u043e\u0447\u043a\u0430\u0445",
    input_message_content=InputTextMessageContent(
        message_text="\u0412\u0432\u0435\u0434\u0438\u0442\u0435 \u0437\u0430\u043f\u0440\u043e\u0441 \u043f\u043e\u0441\u043b\u0435 \u0438\u043c\u0435\u043d\u0438 \u0431\u043e\u0442\u0430, \u0438 \u044f \u043d\u0430\u0439\u0434\u0443 \u043a\u0430\u0440\u0442\u0438\u043d\u043a\u0438.",
    ),
)


def _has_query_text(inline_query: InlineQuery) -> bool:
    return bool(inline_query.query.strip())


def _is_empty_query(inline_query: InlineQuery) -> bool:
    return not _has_query_text(inline_query)


def register_inline_handler(router) -> None:
    @router.inline_query(_is_empty_query)
    async def empty_inline_handler(inline_query: InlineQuery):
        await inline_query.answer(
            results=[EMPTY_QUERY_RESULT],
            button=DEVELOPER_PROFILE_BUTTON,
            cache_time=0,
            is_personal=True,
        )

    @router.inline_query(_has_query_text)
    async def inline_handler(inline_query: InlineQuery):
        try:
            increment_usage()
            query = inline_query.query.strip()
            offset = int(inline_query.offset) if inline_query.offset else 0

            image_data, consumed_count = await search_images(query, start_index=offset + 1, limit=30)

            results = []
            for item in image_data:
                url = item["url"]
                source_url = item.get("source_url")
                
                # Prioritize source page URL, fallback to image URL
                button_url = source_url if source_url else url
                button_text = "🌐 Перейти на сайт" if source_url else "🖼 Открыть оригинал"
                
                reply_markup = InlineKeyboardMarkup(
                    inline_keyboard=[
                        [
                            InlineKeyboardButton(text=button_text, url=button_url)
                        ]
                    ]
                )
                
                if item.get("is_gif"):
                    results.append(
                        InlineQueryResultGif(
                            id=item["id"],
                            gif_url=url,
                            thumbnail_url=url,
                            reply_markup=reply_markup
                        )
                    )
                else:
                    results.append(
                        InlineQueryResultPhoto(
                            id=item["id"],
                            photo_url=url,
                            thumbnail_url=url,
                            reply_markup=reply_markup
                        )
                    )

            next_offset = str(offset + consumed_count) if consumed_count > 0 else ""

            await inline_query.answer(
                results=results,
                next_offset=next_offset,
                button=DEVELOPER_PROFILE_BUTTON,
                cache_time=300,
                is_personal=False,
            )
            record_request(
                user_id=inline_query.from_user.id,
                username=inline_query.from_user.username,
                query=query,
                success=True
            )
        except Exception as exc:
            logging.error("Inline Error: %s", exc)
            increment_error()
            record_request(
                user_id=inline_query.from_user.id,
                username=inline_query.from_user.username,
                query=inline_query.query,
                success=False
            )
            try:
                await inline_query.answer(
                    results=[],
                    cache_time=0,
                )
            except Exception as answer_exc:
                logging.error("Failed to answer inline query after error: %s", answer_exc)
