namespace BidBuilder.Api.Models;

/// <summary>
/// One cost-component line on a <see cref="BoqItem"/> (e.g. an activity's Material
/// or Manpower). <see cref="Value"/> is the canonical figure the rate engine uses:
/// for an Amount-kind type it is money per unit, for a Percent-kind type it is a
/// percentage applied to the item's amount-kind subtotal. At most one line per type.
///
/// For Amount-kind lines the value may be entered as <see cref="Quantity"/> ×
/// <see cref="Rate"/> (e.g. material 100 m² × 50, manpower 40 h × 50); when both are
/// supplied the endpoint sets Value = Quantity × Rate. They are null for a directly
/// typed amount or for a Percent-kind line. Value stays authoritative so the
/// calculator is unaffected by how the figure was entered.
/// </summary>
public class ItemCostComponent : IHasTenant
{
    public int      Id                  { get; set; }
    public Guid     TenantId            { get; set; }
    public int      BoqItemId           { get; set; }
    public int      CostComponentTypeId { get; set; }
    public decimal  Value               { get; set; }   // numeric(18,4) — money (Amount) or % (Percent)

    // ── Optional quantity × rate breakdown (Amount-kind only) ────────────────
    public decimal? Quantity            { get; set; }   // e.g. 100 (m²) or 40 (hours)
    public decimal? Rate                { get; set; }   // money per quantity unit

    // ── Navigation ─────────────────────────────────────────────────────────────
    public BoqItem           BoqItem { get; set; } = null!;
    public CostComponentType Type    { get; set; } = null!;
}
