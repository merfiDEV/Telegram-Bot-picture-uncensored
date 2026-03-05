import logging

from aiogram import F
from aiogram.types import InlineQuery, InlineQueryResultPhoto

from xz.services.bing_images import search_images


def register_inline_handler(router) -> None:
    @router.inline_query(F.query.len() > 0)
    async def inline_handler(inline_query: InlineQuery):
        try:
            query = inline_query.query
            offset = int(inline_query.offset) if inline_query.offset else 0

            image_data = await search_images(query, start_index=offset + 1, limit=30)

            results = []
            for item in image_data:
                results.append(
                    InlineQueryResultPhoto(
                        id=item["id"],
                        photo_url=item["url"],
                        thumbnail_url=item["url"],
                    )
                )

            next_offset = str(offset + 30) if len(image_data) > 0 else ""

            await inline_query.answer(
                results=results,
                next_offset=next_offset,
                cache_time=60,
                is_personal=False,
            )
        except Exception as exc:
            logging.error("Inline Error: %s", exc)
