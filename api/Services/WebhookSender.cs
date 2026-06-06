using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace BidBuilder.Api.Services;

/// <summary>The outcome of one delivery attempt: <c>Ok</c> = a 2xx response,
/// <c>Status</c> = a short label for the row ("200", "timeout", "error: …").</summary>
public readonly record struct WebhookResult(bool Ok, string Status);

/// <summary>
/// 20.9 — Sends a single signed webhook POST. Abstracted behind an interface so
/// tests can capture deliveries without real HTTP (the production impl uses an
/// <see cref="IHttpClientFactory"/> client with a short timeout).
/// </summary>
public interface IWebhookSender
{
    Task<WebhookResult> SendAsync(string url, string eventType, string body, string secret, CancellationToken ct = default);
}

/// <summary>Computes the HMAC-SHA256 signature the receiver verifies, then POSTs.</summary>
public sealed class HttpWebhookSender(IHttpClientFactory httpFactory, ILogger<HttpWebhookSender> logger) : IWebhookSender
{
    /// <summary>The header carrying <c>sha256=&lt;hex&gt;</c> of the raw body, keyed by the subscription secret.</summary>
    public const string SignatureHeader = "X-BidBuilder-Signature";
    public const string EventHeader     = "X-BidBuilder-Event";
    public const string DeliveryHeader  = "X-BidBuilder-Delivery";

    /// <summary>Lowercase hex HMAC-SHA256 of <paramref name="body"/> under <paramref name="secret"/>.</summary>
    public static string Sign(string secret, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    public async Task<WebhookResult> SendAsync(string url, string eventType, string body, string secret, CancellationToken ct = default)
    {
        try
        {
            var client = httpFactory.CreateClient("webhooks");
            client.Timeout = TimeSpan.FromSeconds(5);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation(EventHeader, eventType);
            req.Headers.TryAddWithoutValidation(SignatureHeader, $"sha256={Sign(secret, body)}");
            req.Headers.TryAddWithoutValidation(DeliveryHeader, Guid.NewGuid().ToString());
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await client.SendAsync(req, ct);
            return new WebhookResult((int)resp.StatusCode is >= 200 and < 300, ((int)resp.StatusCode).ToString());
        }
        catch (TaskCanceledException)
        {
            return new WebhookResult(false, "timeout");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook POST to {Url} failed", url);
            var msg = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
            return new WebhookResult(false, $"error: {msg}");
        }
    }
}
