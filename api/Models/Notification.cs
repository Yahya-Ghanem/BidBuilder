namespace BidBuilder.Api.Models;

/// <summary>
/// 20.3 — An in-app notification delivered to a single user. Notifications are
/// per-recipient (one row per user who should see an event), tenant-scoped, and
/// append-only except for the read flag.
///
/// Recipient is a raw int FK to <see cref="User.Id"/> (no enforced navigation,
/// mirroring <see cref="EstimateApproval.ApproverUserId"/>) so the unread-count
/// query — the hot path polled by the bell — needs no join. The Title/Body are
/// denormalised text so rendering never has to re-resolve the source entity
/// (which may have since changed or been deleted).
/// </summary>
public class Notification : IHasTenant
{
    public long Id       { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The user who should see this. References <see cref="User.Id"/>.</summary>
    public int RecipientUserId { get; set; }

    /// <summary>Machine code for the event, e.g. <c>estimate.published</c>. Drives
    /// the icon/grouping on the client; never shown raw.</summary>
    public string  Type  { get; set; } = "";
    public string  Title { get; set; } = "";
    public string? Body  { get; set; }

    /// <summary>Optional in-app link to open when the notification is clicked,
    /// e.g. <c>/projects/3</c>. Relative to the app root.</summary>
    public string? Link { get; set; }

    /// <summary>Loose reference to the source entity (for de-dup / filtering),
    /// e.g. EntityType="Estimate", EntityKey="42". Not a DB foreign key.</summary>
    public string? EntityType { get; set; }
    public string? EntityKey  { get; set; }

    public bool      IsRead    { get; set; }
    public DateTime  CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt    { get; set; }
}
