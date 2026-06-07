namespace BidBuilder.Api.Models;

/// <summary>22.1 — How often a user wants their in-app notifications batched into an
/// email digest. <c>Off</c> (the default) means no digest is ever sent.</summary>
public enum DigestFrequency
{
    Off    = 0,
    Daily  = 1,
    Weekly = 2,
}

/// <summary>
/// 22.1 — A user's notification-digest opt-in. When <see cref="Frequency"/> is Daily or
/// Weekly, <see cref="Services.DigestSchedulerService"/> periodically emails the user a
/// batch of the in-app <see cref="Notification"/> rows they accrued since
/// <see cref="LastSentAt"/> — which doubles as the watermark and the double-send guard
/// (it is advanced atomically even on an empty run). One row per user per tenant; the
/// absence of a row is equivalent to <see cref="DigestFrequency.Off"/>.
/// </summary>
public class NotificationDigestPreference : IHasTenant
{
    public int  Id       { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The user this preference belongs to. References <see cref="User.Id"/>
    /// (raw int, no enforced navigation — mirroring <see cref="Notification.RecipientUserId"/>).</summary>
    public int UserId { get; set; }

    public DigestFrequency Frequency { get; set; } = DigestFrequency.Off;

    /// <summary>When the last digest was sent. Advanced on every send AND on an empty
    /// due-run, so it is both the "notifications since" cursor and the idempotency guard.
    /// Null = a digest has never been sent for this user.</summary>
    public DateTime? LastSentAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
