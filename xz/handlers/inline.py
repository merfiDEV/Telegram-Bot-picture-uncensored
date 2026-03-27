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

            image_data = await search_images(query, start_index=offset + 1, limit=30)

            results = []
            for item in image_data:
                url = item["url"]
                reply_markup = InlineKeyboardMarkup(
                    inline_keyboard=[
                        [
                            InlineKeyboardButton(text="🖼 Открыть оригинал", url=url)
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

            next_offset = str(offset + 30) if len(image_data) > 0 else ""

            await inline_query.answer(
                results=results,
                next_offset=next_offset,
                cache_time=300,
                is_personal=False,
            )
        except Exception as exc:
            logging.error("Inline Error: %s", exc)
            increment_error()
