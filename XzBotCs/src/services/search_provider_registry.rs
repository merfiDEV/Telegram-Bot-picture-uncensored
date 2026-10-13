use crate::models::bot_state::SharedState;
use crate::search_types::BingSearchResponse;
use crate::services::bing_search::BingSearchService;
use crate::services::duckduckgo_search::DuckDuckGoSearchService;
use std::collections::HashMap;
use std::sync::Arc;

pub const BING_KEY: &str = "bing";
pub const DUCKDUCKGO_KEY: &str = "ddg";

/// Аналог Interfaces/IImageSearchProvider.
#[async_trait::async_trait]
pub trait ImageSearchProvider: Send + Sync {
    fn key(&self) -> &str;
    fn display_name(&self) -> &str;
    async fn search_images_detailed(
        &self,
        query: &str,
        start_index: i32,
        limit: i32,
    ) -> BingSearchResponse;
    async fn check(&self) -> (bool, String);
}

pub struct BingSearchProvider {
    service: BingSearchService,
}

impl BingSearchProvider {
    pub fn new(service: BingSearchService) -> Self {
        BingSearchProvider { service }
    }
}

#[async_trait::async_trait]
impl ImageSearchProvider for BingSearchProvider {
    fn key(&self) -> &str {
        BING_KEY
    }
    fn display_name(&self) -> &str {
        "Bing"
    }
    async fn search_images_detailed(
        &self,
        query: &str,
        start_index: i32,
        limit: i32,
    ) -> BingSearchResponse {
        self.service
            .search_images_detailed(query, start_index, limit)
            .await
    }
    async fn check(&self) -> (bool, String) {
        self.service.check_bing().await
    }
}

pub struct DuckDuckGoSearchProvider {
    service: DuckDuckGoSearchService,
}

impl DuckDuckGoSearchProvider {
    pub fn new(service: DuckDuckGoSearchService) -> Self {
        DuckDuckGoSearchProvider { service }
    }
}

#[async_trait::async_trait]
impl ImageSearchProvider for DuckDuckGoSearchProvider {
    fn key(&self) -> &str {
        DUCKDUCKGO_KEY
    }
    fn display_name(&self) -> &str {
        "DuckDuckGo"
    }
    async fn search_images_detailed(
        &self,
        query: &str,
        start_index: i32,
        limit: i32,
    ) -> BingSearchResponse {
        self.service
            .search_images_detailed(query, start_index, limit)
            .await
    }
    async fn check(&self) -> (bool, String) {
        self.service.check_duckduckgo().await
    }
}

/// Аналог SearchProviderRegistry.
pub struct SearchProviderRegistry {
    providers: HashMap<String, Box<dyn ImageSearchProvider>>,
    order: Vec<String>,
    state: SharedState,
}

impl SearchProviderRegistry {
    pub fn new(state: SharedState) -> Self {
        let mut reg = SearchProviderRegistry {
            providers: HashMap::new(),
            order: Vec::new(),
            state,
        };
        reg.register(Box::new(BingSearchProvider::new(BingSearchService::new())));
        reg.register(Box::new(DuckDuckGoSearchProvider::new(
            DuckDuckGoSearchService::new(),
        )));
        reg
    }

    pub fn register(&mut self, provider: Box<dyn ImageSearchProvider>) {
        let key = provider.key().to_lowercase();
        self.providers.insert(key.clone(), provider);
        if !self.order.contains(&key) {
            self.order.push(key);
        }
    }

    /// Ключ провайдера, который используется для автофолбэка (не совпадает с активным).
    pub fn fallback_key(&self) -> Option<String> {
        let active = self.active_key();
        for key in &self.order {
            if !key.eq_ignore_ascii_case(&active) {
                return Some(key.clone());
            }
        }
        None
    }

    /// Все провайдеры, отсортированные: bing первым, затем по display_name.
    pub fn all_keys(&self) -> Vec<(String, String)> {
        let mut list: Vec<(String, String)> = self
            .providers
            .iter()
            .map(|(k, p)| (k.clone(), p.display_name().to_string()))
            .collect();
        list.sort_by(|a, b| {
            let a_bing = a.0 == BING_KEY;
            let b_bing = b.0 == BING_KEY;
            b_bing
                .cmp(&a_bing)
                .then_with(|| a.1.to_lowercase().cmp(&b.1.to_lowercase()))
        });
        list
    }

    /// Проверяет доступность всех провайдеров параллельно.
    pub async fn check_all(&self) -> Vec<(String, String, bool, String)> {
        let keys: Vec<String> = self.providers.keys().cloned().collect();
        let mut futures = Vec::new();
        for key in keys {
            let provider = self.providers.get(&key).unwrap();
            let display = provider.display_name().to_string();
            let (ok, status) = provider.check().await;
            futures.push((key, display, ok, status));
        }
        futures
    }

    /// Ключ активного провайдера; при некорректном значении откатывается на Bing.
    pub fn active_key(&self) -> String {
        let key = {
            let st = self.state.lock().unwrap();
            st.search_provider.clone()
        };
        if key.trim().is_empty() || !self.providers.contains_key(&key.to_lowercase()) {
            return BING_KEY.to_string();
        }
        self.providers
            .get(&key.to_lowercase())
            .map(|p| p.key().to_string())
            .unwrap_or_else(|| BING_KEY.to_string())
    }

    pub fn get(&self, key: Option<&str>) -> Option<&Box<dyn ImageSearchProvider>> {
        let key = key?;
        if key.trim().is_empty() {
            return None;
        }
        self.providers.get(&key.to_lowercase())
    }

    pub fn active(&self) -> &Box<dyn ImageSearchProvider> {
        let key = self.active_key();
        self.providers
            .get(&key)
            .unwrap_or_else(|| &self.providers[BING_KEY])
    }

    pub fn set_active(&self, key: &str) -> bool {
        let lower = key.to_lowercase();
        if !self.providers.contains_key(&lower) {
            return false;
        }
        let canonical = self.providers.get(&lower).unwrap().key().to_string();
        {
            let mut st = self.state.lock().unwrap();
            st.search_provider = canonical;
            st.save();
        }
        true
    }
}

/// Аналог RoutingSearchService — делегирует активному провайдеру + автофолбэк.
pub struct RoutingSearchService {
    registry: Arc<SearchProviderRegistry>,
}

impl RoutingSearchService {
    pub fn new(registry: SearchProviderRegistry) -> Self {
        RoutingSearchService {
            registry: Arc::new(registry),
        }
    }

    pub fn with_registry(registry: Arc<SearchProviderRegistry>) -> Self {
        RoutingSearchService { registry }
    }

    pub fn active_provider_key(&self) -> String {
        self.registry.active_key()
    }

    pub fn registry(&self) -> &SearchProviderRegistry {
        &self.registry
    }

    pub async fn search_images_detailed(
        &self,
        query: &str,
        start_index: i32,
        limit: i32,
    ) -> BingSearchResponse {
        let primary_key = self.registry.active_key();
        let primary = self.registry.active();
        let response = primary
            .search_images_detailed(query, start_index, limit)
            .await;

        if response.items.is_empty() {
            if let Some(fallback_key) = self.registry.fallback_key() {
                if let Some(fallback) = self.registry.get(Some(&fallback_key)) {
                    if !fallback.key().eq_ignore_ascii_case(&primary_key) {
                        crate::log!(
                            "Provider '{}' returned 0 results, falling back to '{}' for '{}'",
                            primary_key,
                            fallback.key(),
                            query
                        );
                        let fallback_response = fallback
                            .search_images_detailed(query, start_index, limit)
                            .await;
                        if !fallback_response.items.is_empty() {
                            return fallback_response;
                        }
                    }
                }
            }
        }

        response
    }

    pub async fn check_bing(&self) -> (bool, String) {
        self.registry.active().check().await
    }
}
