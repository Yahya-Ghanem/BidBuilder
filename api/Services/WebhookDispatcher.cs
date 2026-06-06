using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>The event types a webhook can subscribe to. "*" matches all.</summary>
public static class WebhookEvents
{
    public const string Published   = "estimate.published";
    public const string UnderReview = "estimate.under-review";
    public const string Approved    = "estimate.approved";
    public const string Ping        = "ping";

    /// <summary>Subscribable events surfaced in the UI (ping is delivery-test only).</summary>
    public static readonly string[] Subscribable = { Published, UnderReview, Approved };
}

/// <summary>
/// 20.9 — Fans a domain event out to the tenant's active webhook subscriptions.
/// Like the audit/notification services, call <see cref="DispatchAsync"/> AFTER
/// the triggering operation's own save; all failures are swallowed so a flaky
/// receiver can never break the action. Delivery is signed and the per-row
/// health fields are updated after each attempt.
/// </summary>
public class WebhookDispatcher(AppDbContext db, IWebhookSender sender, ILogger<WebhookDispatcher> logger)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Build a signed envelope and POST it to every active subscription
    /// matching <paramref name="eventType"/>. Returns silently when none match.</summary>
    public async Task DispatchAsync(string eventType, object data)
    {
        try
        {
            var subs = await db.WebhookSubscriptions.Where(w => w.IsActive).ToListAsync();
            var matching = subs.Where(w => Matches(w.Events, eventType)).ToList();
            if (matching.Count == 0) return;
            foreach (var sub in matching)
                await DeliverAsync(sub, eventType, data);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook dispatch failed for {Event}", eventType);
        }
    }

    /// <summary>Deliver one event to one subscription (used by dispatch and the ping endpoint).</summary>
    public async Task<WebhookResult> DeliverAsync(WebhookSubscription sub, string eventType, object data)
    {
        var body = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString(),
            type = eventType,
            occurredAt = DateTime.UtcNow,
            data,
        }, Json);

        var result = await sender.SendAsync(sub.Url, eventType, body, sub.Secret);

        sub.LastStatus = result.Status;
        sub.LastAttemptAt = DateTime.UtcNow;
        sub.FailureCount = result.Ok ? 0 : sub.FailureCount + 1;
        await db.SaveChangesAsync();
        return result;
    }

    /// <summary>Does a subscription's event filter ("*" or a CSV) include this event?</summary>
    static bool Matches(string events, string eventType)
    {
        if (string.IsNullOrWhiteSpace(events) || events.Trim() == "*") return true;
        return events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Any(e => string.Equals(e, eventType, StringComparison.OrdinalIgnoreCase));
    }
}
