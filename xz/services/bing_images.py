import asyncio
import hashlib
import logging
import re
import urllib.parse

import httpx

from xz.stats import increment_error


def get_image_hash(url: str) -> str:
    try:
        clean_url = url.split("?")[0].split("#")[0].lower().strip()
        return hashlib.md5(clean_url.encode("utf-8")).hexdigest()
    except Exception:
        return hashlib.md5(url.encode("utf-8")).hexdigest()


async def is_valid_image(client: httpx.AsyncClient, url: str) -> tuple[bool, bool]:
    try:
        response = await client.head(url, timeout=2.0, follow_redirects=True)
        if response.status_code == 200:
            content_type = response.headers.get("Content-Type", "").lower()
            is_valid = content_type.startswith("image/")
            is_gif = content_type == "image/gif"
            
            # Дополнительная проверка по расширению, если Content-Type не однозначен
            if not is_gif and url.lower().split("?")[0].endswith(".gif"):
                is_gif = True
                
            return is_valid, is_gif
        return False, False
    except Exception:
        return False, False


async def search_images(query: str, start_index: int = 1, limit: int = 50) -> tuple[list[dict], int]:
    bing_filters = ""
    is_gif_search = False
    
    if "--gif" in query:
        is_gif_search = True
        query = query.replace("--gif", "").strip()
        bing_filters += "+filterui:photo-animatedgif"

    encoded_query = urllib.parse.quote(query)
    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
    }
    cookies = {"SRCHHPGUSR": "ADLT=OFF"}

    try:
        fetch_url = f"https://www.bing.com/images/search?q={encoded_query}&adlt=off&first={start_index}"
        if bing_filters:
            fetch_url += f"&qft={bing_filters}"

        async with httpx.AsyncClient(headers=headers, cookies=cookies, timeout=10.0, follow_redirects=True) as client:
            response = await client.get(fetch_url)
            response.raise_for_status()

            links = re.findall(r"murl&quot;:&quot;(.*?)&quot;", response.text)

            unique_results = []
            seen_hashes = set()

            tasks = []
            potential_links = []

            for link in links:
                if not link.startswith("http") or any(bad in link for bad in ["<", ">", "\"", " "]):
                    continue
                potential_links.append(link)
                tasks.append(is_valid_image(client, link))
                if len(tasks) >= limit * 2:
                    break

            validity_results = await asyncio.gather(*tasks)

            for link, (is_ok, is_gif) in zip(potential_links, validity_results):
                if is_ok:
                    if is_gif_search and not is_gif:
                        continue
                        
                    img_hash = get_image_hash(link)
                    if img_hash not in seen_hashes:
                        seen_hashes.add(img_hash)
                        unique_results.append({
                            "url": link,
                            "id": img_hash,
                            "is_gif": is_gif
                        })

                if len(unique_results) >= limit:
                    break

            consumed_count = len(potential_links)
            return unique_results, consumed_count
    except Exception as exc:
        logging.error("Ошибка поиска: %s", exc)
        increment_error()
        return [], 0
