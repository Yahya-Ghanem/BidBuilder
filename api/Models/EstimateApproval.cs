namespace BidBuilder.Api.Models;

/// <summary>
/// 20.2 — A recorded approval of an <see cref="Estimate"/> revision by a single
/// approver. Approvals accumulate per estimate; once their count reaches the
/// tenant's <see cref="TenantSettings.RequiredApprovalsToPublish"/> the estimate
/// may transition Draft/UnderReview → Published.
///
/// Why a separate aggregate (and not a boolean on Estimate):
///   • Multiple sign-offs are the whole point. A boolean would lose the audit
///     trail of WHO approved and WHEN.
///   • Content edits AFTER an approval invalidate every prior approval (the
///     publish would otherwise be on a different bid than what was approved).
///     A wipe-and-recollect cycle is trivial on a child collection, fiddly on
///     a flag.
///
/// Approvals are scoped to the estimate revision: cloning a revision starts
/// fresh (the clone never inherits its source's approvals).
/// </summary>
public class EstimateApproval : IHasTenant
{
    public int   Id         { get; set; }
    public Guid  TenantId   { get; set; }
    public int   EstimateId { get; set; }

    /// <summary>The user who granted the approval. References <see cref="User.Id"/>;
    /// kept as a raw int FK so the row is queryable without a join when listing
    /// approvers (ApproverEmail/Name are denormalized for the audit-style display).</summary>
    public int      ApproverUserId { get; set; }
    public string   ApproverEmail  { get; set; } = "";
    public string   ApproverName   { get; set; } = "";

    public DateTime ApprovedAt     { get; set; } = DateTime.UtcNow;

    /// <summary>Optional sign-off note ("reviewed prelims + markups, OK to publish").</summary>
    public string?  Note           { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Estimate Estimate { get; set; } = null!;
}
