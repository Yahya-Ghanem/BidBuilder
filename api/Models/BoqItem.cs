namespace BidBuilder.Api.Models;

/// <summary>
/// A priced line item in the Bill of Quantities. Its unit rate is either built
/// from an <see cref="Assembly"/> (recipe) or entered ad-hoc. Line total =
/// Quantity × UnitRate. The rate engine fills the cached money fields.
/// </summary>
public class BoqItem : IHasTenant
{
    public int     Id          { get; set; }
    public Guid    TenantId    { get; set; }
    public int     SectionId   { get; set; }

    public string  ItemCode    { get; set; } = "";   // optional client/BOQ ref
    public string  Description { get; set; } = "";
    public string  Unit        { get; set; } = "";   // m, m2, m3, kg, no, LS …
    public decimal Quantity    { get; set; }          // numeric(18,4)
    public int     SortOrder   { get; set; }

    /// <summary>How this line participates in the bid: priced work (marked up),
    /// a provisional/PC sum (in the bid, not marked up), or an alternate (excluded
    /// from the base tender). See <see cref="BoqItemKind"/>.</summary>
    public BoqItemKind Kind   { get; set; } = BoqItemKind.Normal;

    /// <summary>When set, the unit rate is built from this reusable assembly.
    /// When null, <see cref="UnitRate"/> is entered/overridden directly.</summary>
    public int?    AssemblyId  { get; set; }

    /// <summary>Optional project Area this line belongs to (location breakdown);
    /// line totals roll up the area tree. Null = unassigned.</summary>
    public int?    AreaId      { get; set; }

    // Cached money fields (engine-computed; numeric(18,4) rate, numeric(18,2) total)
    public decimal UnitRate    { get; set; }
    public decimal LineTotal   { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public BoqSection Section  { get; set; } = null!;
    public Assembly?  Assembly { get; set; }

    /// <summary>When non-empty, the unit rate is built up from these cost-component
    /// lines (Material + Labor + … + Waste% + Overheads%), taking precedence over
    /// the assembly/ad-hoc rate.</summary>
    public ICollection<ItemCostComponent> CostComponents { get; set; } = new List<ItemCostComponent>();
}
