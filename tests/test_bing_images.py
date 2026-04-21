import pytest
import httpx
from xz.services.bing_images import get_image_hash, search_images

def test_get_image_hash():
    url1 = "https://example.com/image.jpg?v=1"
    url2 = "https://example.com/image.jpg#top"
    url3 = "https://EXAMPLE.COM/image.jpg"
    
    # Hash should be consistent for same base URL
    assert get_image_hash(url1) == get_image_hash(url2)
    assert get_image_hash(url1) == get_image_hash(url3)
    
    # Different URL should have different hash
    assert get_image_hash(url1) != get_image_hash("https://example.com/other.jpg")

@pytest.mark.asyncio
async def test_search_images_integration():
    """Basic integration test to ensure Bing search still returns results."""
    results, consumed = await search_images("test cat", limit=5)
    
    # Should get some results
    assert len(results) > 0
    assert len(results) <= 5
    
    # Each result should have required keys
    for item in results:
        assert "url" in item
        assert "id" in item
        assert "is_gif" in item
        # source_url is optional but usually present in our new logic
        assert "source_url" in item
        assert item["url"].startswith("http")
