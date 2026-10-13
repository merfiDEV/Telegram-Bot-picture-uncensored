use crate::models::bot_state::SharedState;
use crate::services::watermark::WatermarkService;
use base64::Engine;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

/// Аналог Proxy/ProxyServer.cs — HTTP/1.1 раздача картинок с ватермаркой.
pub struct ProxyServer {
    http_client: reqwest::Client,
    watermark_service: Arc<WatermarkService>,
    state: SharedState,
    listener_started: Arc<AtomicBool>,
}

impl ProxyServer {
    pub fn new(
        http_client: reqwest::Client,
        watermark_service: Arc<WatermarkService>,
        state: SharedState,
    ) -> Self {
        ProxyServer {
            http_client,
            watermark_service,
            state,
            listener_started: Arc::new(AtomicBool::new(false)),
        }
    }

    pub fn listener_started(&self) -> Arc<AtomicBool> {
        self.listener_started.clone()
    }

    pub async fn start(self: Arc<Self>, port: u16, mut ct: tokio::sync::watch::Receiver<bool>) {
        let listener = match TcpListener::bind(("0.0.0.0", port)).await {
            Ok(l) => l,
            Err(ex) => {
                self.listener_started.store(false, Ordering::SeqCst);
                crate::log!("Listener error: {}", ex);
                crate::log!("Watermark proxy disabled. Inline results will use original image URLs.");
                return;
            }
        };

        self.listener_started.store(true, Ordering::SeqCst);
        crate::log!("Proxy started on port {}", port);

        loop {
            tokio::select! {
                _ = ct.changed() => {
                    if *ct.borrow() {
                        break;
                    }
                }
                accept = listener.accept() => {
                    match accept {
                        Ok((stream, _)) => {
                            let this = self.clone();
                            tokio::spawn(async move {
                                this.handle_client(stream).await;
                            });
                        }
                        Err(_) => break,
                    }
                }
            }
        }

        self.listener_started.store(false, Ordering::SeqCst);
        crate::log!("Proxy listener stopped.");
    }

    async fn handle_client(self: Arc<Self>, mut stream: tokio::net::TcpStream) {
        let request_text = match read_http_request(&mut stream).await {
            Ok(t) => t,
            Err(_) => return,
        };

        let b64_url = match extract_proxy_url_parameter(&request_text) {
            Some(u) => u,
            None => {
                write_text_response(&mut stream, 400, "Bad Request", "Missing u parameter").await;
                return;
            }
        };

        let url = match base64::engine::general_purpose::STANDARD.decode(&b64_url) {
            Ok(bytes) => match String::from_utf8(bytes) {
                Ok(s) => s,
                Err(_) => {
                    write_text_response(&mut stream, 400, "Bad Request", "Bad base64").await;
                    return;
                }
            },
            Err(_) => {
                write_text_response(&mut stream, 400, "Bad Request", "Bad base64").await;
                return;
            }
        };

        let request = self
            .http_client
            .get(&url)
            .header("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")
            .header("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");

        let image_response = match request.send().await {
            Ok(r) => r,
            Err(ex) => {
                crate::log!("Proxy error: {}", ex);
                write_text_response(&mut stream, 500, "Internal Server Error", "Proxy error").await;
                return;
            }
        };

        let status = image_response.status();
        if !status.is_success() {
            write_text_response(
                &mut stream,
                status.as_u16(),
                status.canonical_reason().unwrap_or("Upstream Error"),
                "Upstream image request failed",
            )
            .await;
            return;
        }

        let original_content_type = image_response
            .headers()
            .get(reqwest::header::CONTENT_TYPE)
            .and_then(|v| v.to_str().ok())
            .map(|s| s.split(';').next().unwrap_or(s).trim().to_string())
            .unwrap_or_else(|| "application/octet-stream".to_string());

        if !original_content_type.to_lowercase().starts_with("image/") {
            write_text_response(
                &mut stream,
                415,
                "Unsupported Media Type",
                "Upstream response is not an image",
            )
            .await;
            return;
        }

        let bytes = match image_response.bytes().await {
            Ok(b) => b.to_vec(),
            Err(ex) => {
                crate::log!("Proxy error: {}", ex);
                write_text_response(&mut stream, 500, "Internal Server Error", "Proxy error").await;
                return;
            }
        };

        let watermark_text = {
            let st = self.state.lock().unwrap();
            st.watermark_text.clone()
        };

        let result = self.watermark_service.apply_watermark_or_original(
            &bytes,
            &original_content_type,
            &watermark_text,
        );

        if !result.is_watermarked {
            write_redirect_response(&mut stream, &url).await;
            return;
        }

        write_binary_response(&mut stream, &result.content_type, &result.bytes).await;
    }
}

async fn read_http_request(stream: &mut tokio::net::TcpStream) -> Result<String, std::io::Error> {
    let mut buffer = vec![0u8; 8192];
    let mut total = 0;
    loop {
        if total >= buffer.len() {
            break;
        }
        let read = stream.read(&mut buffer[total..]).await?;
        if read == 0 {
            break;
        }
        total += read;
        let current = String::from_utf8_lossy(&buffer[..total]);
        if current.contains(
            "

",
        ) {
            return Ok(current.to_string());
        }
    }
    Ok(String::from_utf8_lossy(&buffer[..total]).to_string())
}

fn extract_proxy_url_parameter(request_text: &str) -> Option<String> {
    let first_line = request_text
        .split(
            "
",
        )
        .next()
        .unwrap_or("");
    let parts: Vec<&str> = first_line.split(' ').collect();
    if parts.len() < 2 {
        return None;
    }
    let target = parts[1];
    let lower = target.to_lowercase();
    let query_index = lower.find("?u=")?;
    let mut value = &target[query_index + 3..];
    if let Some(amp) = value.find('&') {
        value = &value[..amp];
    }
    Some(percent_decode(value))
}

fn percent_decode(s: &str) -> String {
    urlencoding::decode(s)
        .map(|c| c.into_owned())
        .unwrap_or_else(|_| s.to_string())
}

async fn write_binary_response(
    stream: &mut tokio::net::TcpStream,
    content_type: &str,
    bytes: &[u8],
) {
    let headers = format!(
        "HTTP/1.1 200 OK
Content-Type: {}
Content-Length: {}
Cache-Control: public, max-age=86400
Connection: close

",
        content_type,
        bytes.len()
    );
    let _ = stream.write_all(headers.as_bytes()).await;
    let _ = stream.write_all(bytes).await;
    let _ = stream.flush().await;
}

async fn write_redirect_response(stream: &mut tokio::net::TcpStream, location: &str) {
    let headers = format!(
        "HTTP/1.1 302 Found
Location: {}
Content-Length: 0
Connection: close

",
        location
    );
    let _ = stream.write_all(headers.as_bytes()).await;
    let _ = stream.flush().await;
}

async fn write_text_response(
    stream: &mut tokio::net::TcpStream,
    status_code: u16,
    reason: &str,
    body: &str,
) {
    let body_bytes = body.as_bytes();
    let headers = format!(
        "HTTP/1.1 {} {}
Content-Type: text/plain; charset=utf-8
Content-Length: {}
Connection: close

",
        status_code,
        reason,
        body_bytes.len()
    );
    let _ = stream.write_all(headers.as_bytes()).await;
    let _ = stream.write_all(body_bytes).await;
    let _ = stream.flush().await;
}
