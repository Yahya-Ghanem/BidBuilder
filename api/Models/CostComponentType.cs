namespace BidBuilder.Api.Models;

/// <summary>
/// A tenant-managed cost-component category used to build up a BOQ item's unit
/// rate (e.g. Material, Labor, Equipment, Waste, Overheads — and any custom type
/// the tenant adds, like Transport or Insurance). <see cref="CalcKind"/> decides
/// whether a line of this type is an absolute amount or a percentage of the
/// amount-kind subtotal. Built-in types are seeded per tenant and not deletable.
/// </summary>
public class CostComponentType : IHasTenant
{
    public int          Id        { get; set; }
    public Guid         TenantId  { get; set; }
    public string       Code      { get; set; } = "";   // short, unique within tenant
    public string       Name      { get; set; } = "";
    public CostCalcKind CalcKind  { get; set; } = CostCalcKind.Amount;
    public int          SortOrder { get; set; }
    public bool         IsActive  { get; set; } = true;
    public bool         Builtin   { get; set; }          // seeded defaults — cannot be deleted
    public DateTime     UpdatedAt { get; set; } = DateTime.UtcNow;
}
