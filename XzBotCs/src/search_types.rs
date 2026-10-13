use chrono::Duration;

/// Аналог Services/BingSearchService.cs -> BingImageResult.
#[derive(Debug, Clone)]
pub struct BingImageResult {
    pub url: String,
    pub thumbnail_url: String,
    pub source_url: String,
    pub id: String,
    pub is_gif: bool,
}

impl BingImageResult {
    pub fn new(
        url: String,
        thumbnail_url: String,
        source_url: String,
        id: String,
        is_gif: bool,
    ) -> Self {
        BingImageResult {
            url,
            thumbnail_url,
            source_url,
            id,
            is_gif,
        }
    }
}

/// Аналог BingSearchResponse.
#[derive(Debug, Clone)]
pub struct BingSearchResponse {
    pub items: Vec<BingImageResult>,
    pub consumed_count: usize,
    pub response_time: Duration,
    pub error_type: Option<String>,
}

impl Default for BingSearchResponse {
    fn default() -> Self {
        BingSearchResponse {
            items: Vec::new(),
            consumed_count: 0,
            response_time: Duration::zero(),
            error_type: None,
        }
    }
}
