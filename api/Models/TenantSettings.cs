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

    // ── Estimating defaults (seed new projects) ──────────────────────────────
    public string  BaseCurrency       { get; set; } = "AED"; // ISO-4217
    public decimal DefaultOverheadPct  { get; set; }          // e.g. 8.00
    public decimal DefaultProfitPct     { get; set; }         // e.g. 12.00
    public decimal DefaultContingencyPct { get; set; }        // e.g. 5.00
    public decimal DefaultTaxRatePct     { get; set; }        // e.g. 5.00 (VAT) — pre-fills new estimates

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Tenant Tenant { get; set; } = null!;
}
