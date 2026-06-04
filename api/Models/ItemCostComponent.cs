namespace BidBuilder.Api.Models;

/// <summary>
/// One cost-component line on a <see cref="BoqItem"/>: the value entered for a
/// given <see cref="CostComponentType"/>. For an Amount-kind type the value is
/// money per unit; for a Percent-kind type it is a percentage applied to the
/// item's amount-kind subtotal. At most one line per type per item.
/// </summary>
public class ItemCostComponent : IHasTenant
{
    public int      Id                  { get; set; }
    public Guid     TenantId            { get; set; }
    public int      BoqItemId           { get; set; }
    public int      CostComponentTypeId { get; set; }
    public decimal  Value               { get; set; }   // numeric(18,4)

    // ── Navigation ─────────────────────────────────────────────────────────────
    public BoqItem           BoqItem { get; set; } = null!;
    public CostComponentType Type    { get; set; } = null!;
}
