namespace BidBuilder.Api.Models;

/// <summary>
/// 1:1 with <see cref="Tenant"/>. Company profile + estimating defaults that
/// pre-fill new projects (base currency, default markup percentages, locale).
/// </summary>
public class TenantSettings : IHasTenant
{
    public int    Id       { get; set; }
    public Guid   TenantId { get; set; }

    // ── Company profile ──────────────────────────────────────────────────────
    public string? Website      { get; set; }
    public string? ContactEmail { get; set; }
    public string? Phone        { get; set; }
    public string? Address      { get; set; }
    public string? City         { get; set; }
    public string? Country      { get; set; }   // ISO-2
    public string  Timezone     { get; set; } = "UTC";

    // ── Branding (appears on exported bid documents) ─────────────────────────
    public byte[]? LogoBytes       { get; set; }
    public string? LogoContentType { get; set; }   // image/png | image/jpeg

    /// <summary>24.5 — Multi-paragraph header text rendered above the body on bid letters
    /// (e.g. company tagline, accreditations). Newlines are preserved; ** bold ** spans
    /// are honoured by the PDF renderer. Stored sanitized as plain text; max ~2 KB.</summary>
    public string? BrandHeaderText { get; set; }

    /// <summary>24.5 — Footer text on bid letters / branded exports (e.g. registered address,
    /// confidentiality notice). Same encoding rules as BrandHeaderText.</summary>
    public string? BrandFooterText { get; set; }

    /// <summary>24.5 — Sign-off block used by the bid letter when the user doesn't override
    /// per-letter. Lines like "Yours faithfully, / Acme Construction LLC / Procurement".</summary>
    public string? BrandSignatureText { get; set; }

    // ── Estimating defaults (seed new projects) ──────────────────────────────
    public string  BaseCurrency       { get; set; } = "AED"; // ISO-4217
    public decimal DefaultOverheadPct  { get; set; }          // e.g. 8.00
    public decimal DefaultProfitPct     { get; set; }         // e.g. 12.00
    public decimal DefaultContingencyPct { get; set; }        // e.g. 5.00
    public decimal DefaultTaxRatePct     { get; set; }        // e.g. 5.00 (VAT) — pre-fills new estimates

    /// <summary>20.2 — Number of <see cref="EstimateApproval"/> rows required
    /// before an estimate revision may transition Draft/UnderReview → Published.
    /// Zero (default) keeps the legacy "anyone with permission can publish"
    /// behavior; a positive value enforces N-of-anyone sign-off. The status
    /// PUT returns 409 with details if the threshold isn't met.</summary>
    public int RequiredApprovalsToPublish { get; set; }

    /// <summary>21.1 — When true (default), the tenant's users receive an email copy
    /// of the in-app notifications they're sent (publish / under-review / approval /
    /// subcontractor-quote events), and subcontractor RFQ invites are emailed. A tenant
    /// admin can turn this off without affecting the platform's global SMTP transport.
    /// Email is only ever sent when the platform SMTP transport is also configured.</summary>
    public bool NotificationEmailsEnabled { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Tenant Tenant { get; set; } = null!;
}
