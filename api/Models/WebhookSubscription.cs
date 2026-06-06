namespace BidBuilder.Api.Models;

/// <summary>
/// 20.9 — A tenant-configured outbound webhook. When a subscribed event fires,
/// the API POSTs a signed JSON envelope to <see cref="Url"/>. The
/// <see cref="Secret"/> is shared once at creation and used to compute the
/// HMAC-SHA256 signature the receiver verifies; it is never returned again.
///
/// <see cref="Events"/> is a comma-separated list of event types, or "*" to
/// receive everything. Delivery health is denormalised onto the row
/// (<see cref="LastStatus"/> / <see cref="LastAttemptAt"/> /
/// <see cref="FailureCount"/>) so the management UI can show "is it working?"
/// without a separate delivery-log table.
/// </summary>
public class WebhookSubscription : IHasTenant
{
    public int  Id       { get; set; }
    public Guid TenantId { get; set; }

    public string Url    { get; set; } = "";
    public string Secret { get; set; } = "";
    /// <summary>CSV of event types (e.g. "estimate.published,estimate.approved") or "*".</summary>
    public string Events { get; set; } = "*";
    public bool   IsActive { get; set; } = true;

    public DateTime  CreatedAt     { get; set; } = DateTime.UtcNow;
    /// <summary>Last delivery outcome — an HTTP status ("200") or a short error
    /// ("timeout", "error: ..."). Null until the first delivery is attempted.</summary>
    public string?   LastStatus    { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    /// <summary>Consecutive failures (reset to 0 on a 2xx). Lets the UI flag a dead endpoint.</summary>
    public int       FailureCount  { get; set; }
}
