namespace BidBuilder.Api.Models;

/// <summary>
/// A division/section in the Bill of Quantities tree (e.g. "Concrete Works").
/// Supports one level of nesting via <see cref="ParentSectionId"/> so a section
/// can hold sub-sections. Holds <see cref="BoqItem"/> leaf rows.
/// </summary>
public class BoqSection : IHasTenant
{
    public int     Id              { get; set; }
    public Guid    TenantId        { get; set; }
    public int     EstimateId      { get; set; }
    public int?    ParentSectionId { get; set; }   // null = top-level division

    public string  Code            { get; set; } = "";   // e.g. "03" (MasterFormat-style)
    public string  Title           { get; set; } = "";
    public int     SortOrder       { get; set; }

    /// <summary>Cached sum of child item line totals (numeric(18,2)).</summary>
    public decimal SectionTotal    { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Estimate            Estimate { get; set; } = null!;
    public ICollection<BoqItem> Items   { get; set; } = new List<BoqItem>();
}
