import asyncio
import hashlib
import logging
import re
import time
import urllib.parse

import httpx

from xz.stats import record_error, record_request_time


def get_image_hash(url: str) -> str:
    try:
        clean_url = url.split("?")[0].split("#")[0].lower().strip()
        return hashlib.md5(clean_url.encode("utf-8")).hexdigest()
    except Exception:
        return hashlib.md5(url.encode("utf-8")).hexdigest()


def _parse_image_response(url: str, response: httpx.Response) -> tuple[bool, bool]:
    content_type = response.headers.get("Content-Type", "").lower().strip()
    is_valid = content_type.startswith("image/")
    is_gif = content_type == "image/gif"

    # Fallback to the URL extension if the origin serves an ambiguous media type.
    if not is_gif and url.lower().split("?")[0].endswith(".gif"):
        is_gif = True

    return is_valid, is_gif


async def _request_image_metadata(
    client: httpx.AsyncClient,
    method: str,
    url: str,
    *,
    headers: dict[str, str] | None = None,
) -> httpx.Response:
    return await client.request(
        method,
        url,
        headers=headers,
        timeout=2.0,
        follow_redirects=True,
    )


async def is_valid_image(client: httpx.AsyncClient, url: str) -> tuple[bool, bool]:
    try:
        response = await _request_image_metadata(client, "HEAD", url)
    except httpx.HTTPError as exc:
        logging.info("HEAD failed for %s, falling back to GET: %s", url, type(exc).__name__)
    else:
        if response.status_code == 200:
            content_type = response.headers.get("Content-Type", "").lower().strip()
            if content_type:
                return _parse_image_response(url, response)

            logging.info("HEAD returned empty Content-Type for %s, falling back to GET", url)
        elif response.status_code in {403, 405}:
            logging.info("HEAD returned %s for %s, falling back to GET", response.status_code, url)
        else:
            return False, False

    fallback_requests = [
        ("range_get", {"Range": "bytes=0-0"}),
        ("plain_get", None),
    ]
    for fallback_name, headers in fallback_requests:
        try:
            response = await _request_image_metadata(client, "GET", url, headers=headers)
        except httpx.HTTPError as exc:
            logging.info("%s failed for %s: %s", fallback_name, url, type(exc).__name__)
            continue

        if response.status_code != 200:
            logging.info("%s returned %s for %s", fallback_name, response.status_code, url)
            continue

        content_type = response.headers.get("Content-Type", "").lower().strip()
        if not content_type:
            logging.info("%s returned empty Content-Type for %s", fallback_name, url)
            continue

        logging.info("%s succeeded for %s", fallback_name, url)
        return _parse_image_response(url, response)

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
            request_start = time.monotonic()
            response = await client.get(fetch_url)
            request_elapsed = time.monotonic() - request_start
            record_request_time(request_elapsed)
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
    except httpx.TimeoutException:
        logging.error("Таймаут запроса: %s", query)
        record_error("timeout")
        return [], 0
    except httpx.HTTPError as exc:
        logging.error("HTTP ошибка при поисте: %s", exc)
        record_error("http_error")
        return [], 0
    except re.error as exc:
        logging.error("Ошибка парсинга ответа Bing: %s", exc)
        record_error("parse_error")
        return [], 0
    except Exception as exc:
        logging.error("Неизвестная ошибка при поиске: %s", exc)
        record_error("unknown")
        return [], 0
