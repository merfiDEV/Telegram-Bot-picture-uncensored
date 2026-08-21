using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XzBotCs.Interfaces;
using XzBotCs.Models;

namespace XzBotCs
{
    public class ProxyServer
    {
        private readonly IWatermarkService _watermarkService;
        private readonly BotState _state;
        private readonly HttpClient _httpClient;

        public volatile bool IsRunning;

        public ProxyServer(IWatermarkService watermarkService, BotState state, HttpClient httpClient)
        {
            _watermarkService = watermarkService;
            _state = state;
            _httpClient = httpClient;
        }

        public async Task StartAsync(int port, CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
                IsRunning = true;
                Console.WriteLine($"Proxy started on port {port}");

                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
            }
            catch (OperationCanceledException)
            {
                IsRunning = false;
                Console.WriteLine("Proxy listener stopped.");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                Console.WriteLine($"Listener error: {ex.Message}");
                Console.WriteLine("Watermark proxy disabled. Inline results will use original image URLs.");
            }
            finally
            {
                listener.Stop();
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            await using var stream = client.GetStream();
            using (client)
            {
                try
                {
                    string requestText = await ReadHttpRequestAsync(stream, ct);
                    string? b64Url = ExtractProxyUrlParameter(requestText);
                    if (string.IsNullOrEmpty(b64Url))
                    {
                        await WriteTextResponseAsync(stream, 400, "Bad Request", "Missing u parameter", ct);
                        return;
                    }

                    string url = Encoding.UTF8.GetString(Convert.FromBase64String(b64Url));
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                    request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");

                    using var imageResponse = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!imageResponse.IsSuccessStatusCode)
                    {
                        await WriteTextResponseAsync(stream, (int)imageResponse.StatusCode, imageResponse.ReasonPhrase ?? "Upstream Error", "Upstream image request failed", ct);
                        return;
                    }

                    string originalContentType = imageResponse.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                    if (!originalContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteTextResponseAsync(stream, 415, "Unsupported Media Type", "Upstream response is not an image", ct);
                        return;
                    }

                    var bytes = await imageResponse.Content.ReadAsByteArrayAsync(ct);
                    var result = _watermarkService.ApplyWatermarkOrOriginal(bytes, originalContentType, _state.WatermarkText);
                    if (!result.IsWatermarked)
                    {
                        await WriteRedirectResponseAsync(stream, url, ct);
                        return;
                    }

                    await WriteBinaryResponseAsync(stream, result.ContentType, result.Bytes, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Proxy error: {ex.Message}");
                    await WriteTextResponseAsync(stream, 500, "Internal Server Error", "Proxy error", CancellationToken.None);
                }
            }
        }

        private static async Task<string> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            int total = 0;

            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (read <= 0) break;

                total += read;
                string current = Encoding.ASCII.GetString(buffer, 0, total);
                if (current.Contains("\r\n\r\n")) return current;
            }

            return Encoding.ASCII.GetString(buffer, 0, total);
        }

        private static string? ExtractProxyUrlParameter(string requestText)
        {
            string firstLine = requestText.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? "";
            string[] parts = firstLine.Split(' ');
            if (parts.Length < 2) return null;

            string target = parts[1];
            int queryIndex = target.IndexOf("?u=", StringComparison.OrdinalIgnoreCase);
            if (queryIndex < 0) return null;

            string value = target.Substring(queryIndex + 3);
            int ampIndex = value.IndexOf('&');
            if (ampIndex >= 0) value = value.Substring(0, ampIndex);

            return WebUtility.UrlDecode(value);
        }

        private static async Task WriteBinaryResponseAsync(NetworkStream stream, string contentType, byte[] bytes, CancellationToken ct)
        {
            string headers =
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bytes.Length}\r\n" +
                "Cache-Control: public, max-age=86400\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
            await stream.WriteAsync(bytes, ct);
        }

        private static async Task WriteRedirectResponseAsync(NetworkStream stream, string location, CancellationToken ct)
        {
            string headers =
                "HTTP/1.1 302 Found\r\n" +
                $"Location: {location}\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
        }

        private static async Task WriteTextResponseAsync(NetworkStream stream, int statusCode, string reason, string body, CancellationToken ct)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers =
                $"HTTP/1.1 {statusCode} {reason}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
            await stream.WriteAsync(bodyBytes, ct);
        }
    }
}
