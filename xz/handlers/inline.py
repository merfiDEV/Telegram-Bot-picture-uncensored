import logging

from aiogram import F
from aiogram.types import InlineQuery, InlineQueryResultPhoto, InlineQueryResultGif, InlineKeyboardButton, InlineKeyboardMarkup

from xz.services.bing_images import search_images
from xz.stats import increment_error, increment_usage


def register_inline_handler(router) -> None:
    @router.inline_query(F.query.len() > 0)
    async def inline_handler(inline_query: InlineQuery):
        try:
            increment_usage()
            query = inline_query.query
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
                cache_time=300,
                is_personal=False,
            )
        except Exception as exc:
            logging.error("Inline Error: %s", exc)
            increment_error()
            try:
                await inline_query.answer(
                    results=[],
                    cache_time=0,
                )
            except Exception as answer_exc:
                logging.error("Failed to answer inline query after error: %s", answer_exc)
