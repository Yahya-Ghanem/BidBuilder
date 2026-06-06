namespace BidBuilder.Api.Models;

/// <summary>
/// 20.6 — A request-for-quote sent to an external subcontractor, plus the quote
/// they return. An estimator creates one against a <see cref="Project"/>, naming
/// the trade/package and the scope to price; the system mints an unguessable
/// <see cref="Token"/> and a public portal link. The subcontractor opens that
/// link (NO login), reads the scope, and submits a price — landing it here for
/// the estimator to accept or decline.
///
/// The token is the only credential on the public side and is globally unique
/// (an index without the tenant prefix), so the anonymous portal endpoint can
/// resolve the owning tenant from the token alone — see
/// <c>SubcontractorQuoteEndpoints</c> and the <c>/api/portal</c> exemption in
/// <c>TenantResolutionMiddleware</c>.
/// </summary>
public class SubcontractorQuote : IHasTenant
{
    public int    Id        { get; set; }
    public Guid   TenantId  { get; set; }

    /// <summary>The project this RFQ belongs to. Cascade-deleted with the project.</summary>
    public int    ProjectId { get; set; }

    /// <summary>Unguessable, URL-safe capability token embedded in the portal link.
    /// Globally unique so the public endpoint can find the row without a tenant.</summary>
    public string Token     { get; set; } = "";

    /// <summary>Trade / work package, e.g. "Concrete works" or "MEP — HVAC".</summary>
    public string Trade     { get; set; } = "";

    /// <summary>What the subcontractor is being asked to price (free text).</summary>
    public string Scope     { get; set; } = "";

    public string Currency  { get; set; } = "AED";

    /// <summary>Who the RFQ was addressed to (display only, set by the estimator).</summary>
    public string  ContractorName  { get; set; } = "";
    public string? ContractorEmail { get; set; }

    /// <summary>After this instant the portal refuses new/updated submissions.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Pending · Submitted · Accepted · Declined · Revoked.
    /// (Expiry is derived from <see cref="ExpiresAt"/>, not a stored status.)</summary>
    public SubcontractorQuoteStatus Status { get; set; } = SubcontractorQuoteStatus.Pending;

    // ── Submission (filled in via the public portal) ───────────────────────────
    public decimal?  QuotedAmount    { get; set; }
    public string?   SubmissionNotes { get; set; }
    /// <summary>Contact the subcontractor confirms at submission time.</summary>
    public string?   RespondentName  { get; set; }
    public DateTime? SubmittedAt     { get; set; }

    // ── Decision (estimator accept/decline) ────────────────────────────────────
    public int?      DecidedByUserId { get; set; }
    public DateTime? DecidedAt       { get; set; }

    public DateTime  CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Project Project { get; set; } = null!;
}

public enum SubcontractorQuoteStatus
{
    Pending,    // invite sent, awaiting the subcontractor's submission
    Submitted,  // subcontractor has priced it; awaiting the estimator's decision
    Accepted,   // estimator accepted this quote
    Declined,   // estimator declined this quote
    Revoked,    // estimator cancelled the invite; the portal link is now dead
}
