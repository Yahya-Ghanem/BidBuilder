using System.Collections.Concurrent;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.9 — Test double for <see cref="IWebhookSender"/>. Records every delivery
/// (always "succeeding" with 200) so tests can assert what would have been POSTed
/// without standing up a real receiver. Deliveries are keyed by URL, so each test
/// uses a unique URL to isolate its own captures from the shared host.
/// </summary>
public sealed class CapturingWebhookSender : IWebhookSender
{
    public readonly record struct Sent(string Url, string EventType, string Body, string Secret);

    private readonly ConcurrentQueue<Sent> _sent = new();

    public IReadOnlyList<Sent> ForUrl(string url) => _sent.Where(s => s.Url == url).ToList();

    public Task<WebhookResult> SendAsync(string url, string eventType, string body, string secret, CancellationToken ct = default)
    {
        _sent.Enqueue(new Sent(url, eventType, body, secret));
        return Task.FromResult(new WebhookResult(true, "200"));
    }
}
