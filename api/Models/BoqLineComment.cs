namespace BidBuilder.Api.Models;

/// <summary>
/// 27.1 — A comment on a single BOQ line (<see cref="BoqItem"/>). Estimators use these
/// to discuss a line ("is this rate including waste?") instead of taking the conversation
/// to Slack/email. @mentions on a new comment fire an in-app <see cref="Notification"/>
/// (20.3) to the mentioned users.
///
/// A comment can be <b>resolved</b> (<see cref="ResolvedAt"/> set) to drop it out of the
/// open-comments count shown on the row, without deleting the audit trail. Author identity
/// is denormalised (<see cref="AuthorUserId"/> + email/name) the same way
/// <see cref="EstimateApproval"/> does, so listing a thread needs no join to Users and the
/// row survives a later user deactivation.
///
/// The comment hangs off the BOQ item (cascade-deletes with it). The owning estimate is
/// reached via <c>BoqItem.Section.EstimateId</c> — <see cref="BoqItem"/> has no direct
/// EstimateId. Comments are deliberately NOT bid content, so adding one never invalidates
/// the estimate's approvals.
/// </summary>
public class BoqLineComment : IHasTenant
{
    public int  Id        { get; set; }
    public Guid TenantId  { get; set; }

    /// <summary>The BOQ line this comment is attached to (<see cref="BoqItem.Id"/>).</summary>
    public int BoqItemId { get; set; }

    /// <summary>The comment author. References <see cref="User.Id"/> (raw int FK, no nav).</summary>
    public int    AuthorUserId { get; set; }
    public string AuthorEmail  { get; set; } = "";
    public string AuthorName   { get; set; } = "";

    public string Body { get; set; } = "";

    /// <summary>Optional parent for a threaded reply (null = top-level comment).</summary>
    public int? ParentCommentId { get; set; }

    public DateTime  CreatedAt  { get; set; } = DateTime.UtcNow;

    /// <summary>Null = open (counts toward the row's open-comments badge); set = resolved.</summary>
    public DateTime? ResolvedAt { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public BoqItem BoqItem { get; set; } = null!;
}
