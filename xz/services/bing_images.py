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


async def is_valid_image(client: httpx.AsyncClient, url: str) -> bool:
    try:
        if not any(url.lower().endswith(ext) for ext in [".jpg", ".jpeg", ".png", ".webp"]):
            pass

        response = await client.head(url, timeout=2.0, follow_redirects=True)
        if response.status_code == 200:
            content_type = response.headers.get("Content-Type", "")
            return content_type.startswith("image/")
        return False
    except Exception:
        return False


async def search_images(query: str, start_index: int = 1, limit: int = 50):
    bing_filters = ""
    if "--gif" in query:
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

            for link, is_ok in zip(potential_links, validity_results):
                if is_ok:
                    img_hash = get_image_hash(link)
                    if img_hash not in seen_hashes:
                        seen_hashes.add(img_hash)
                        unique_results.append({
                            "url": link,
                            "id": img_hash,
                        })

                if len(unique_results) >= limit:
                    break

            return unique_results
    except Exception as exc:
        logging.error("Ошибка поиска: %s", exc)
        increment_error()
        return []
