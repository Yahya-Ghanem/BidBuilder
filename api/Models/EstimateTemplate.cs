namespace BidBuilder.Api.Models;

/// <summary>
/// 21.2 → 21.3 — A reusable, tenant-scoped estimate template. Captures a point-in-time
/// snapshot of an estimate's structure (BOQ sections/items + their cost build-ups,
/// preliminaries, markups, risks, and estimate-level meta) as a JSON payload, so a new
/// estimate can be spun up pre-populated for projects that bid similar work.
///
/// The structure is stored as a denormalised JSON blob rather than mirrored child
/// tables: a template is a snapshot, never edited field-by-field, so one column keeps
/// the schema small. Area tags are intentionally NOT captured — areas are project-scoped.
/// </summary>
public class EstimateTemplate : IHasTenant
{
    public int  Id       { get; set; }
    public Guid TenantId { get; set; }

    public string  Name        { get; set; } = "";
    public string? Description  { get; set; }

    /// <summary>Serialized <c>TemplatePayload</c> (see EstimateTemplateService).</summary>
    public string PayloadJson { get; set; } = "";

    /// <summary>Denormalised counts for the list view (so it needn't parse the payload).</summary>
    public int SectionCount { get; set; }
    public int ItemCount    { get; set; }

    // ── 22.3 Library metadata (browse/filter; never affects the applied estimate) ──
    /// <summary>Optional grouping label, e.g. "Warehouse" / "Fit-out". Free text, one per template.</summary>
    public string? Category { get; set; }

    /// <summary>Optional free-form tags, stored comma-separated (e.g. "concrete,framing").
    /// Matched by exact membership (not substring) on the library filter.</summary>
    public string? Tags { get; set; }

    /// <summary>22.3 — Pinned by a tenant admin so it sorts to the top of the picker as a
    /// recommended/standard template for the org. Purely a sort + badge; no access change.</summary>
    public bool IsFeatured { get; set; }

    public int      CreatedByUserId { get; set; }
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
}
