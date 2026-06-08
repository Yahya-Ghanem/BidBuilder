namespace BidBuilder.Api.Models;

/// <summary>
/// 27.5 — Per-user personalisation of the projects sidebar. One row per user per tenant.
/// Holds the user's pinned (favourited) project ids and a most-recently-visited list,
/// both stored as a comma-separated list of project ids (most-recent-first for
/// <see cref="RecentProjectIds"/>). The absence of a row means "no pins, no recents".
///
/// This is deliberately a single per-user row (mirroring
/// <see cref="NotificationDigestPreference"/>) rather than a per-(user,project) join
/// table: the lists are short, only ever read/written as a whole for the current user,
/// and never need a per-project SQL join. <see cref="UserId"/> is a raw int reference to
/// <see cref="User.Id"/> with no enforced navigation — consistent with the other
/// user-scoped tables in this app.
/// </summary>
public class UserPreferences : IHasTenant
{
    public int  Id       { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The user these preferences belong to (<see cref="User.Id"/>).</summary>
    public int UserId { get; set; }

    /// <summary>Comma-separated project ids the user has pinned, e.g. "3,7,12".
    /// Order is insertion order; rendering sorts by the project list.</summary>
    public string PinnedProjectIds { get; set; } = "";

    /// <summary>Comma-separated project ids, most-recently-visited first, capped at
    /// <see cref="RecentCap"/>. The sidebar shows the leading few.</summary>
    public string RecentProjectIds { get; set; } = "";

    public DateTime  CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>How many recent ids we retain (the UI shows the first 5).</summary>
    public const int RecentCap = 12;
}
