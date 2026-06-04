namespace BidBuilder.Api.Models;

/// <summary>
/// Labor resource (a trade/crew). Rate is cost per hour in the tenant base
/// currency. Shared across all projects in the tenant.
/// </summary>
public class LaborResource : IHasTenant
{
    public int     Id        { get; set; }
    public Guid    TenantId  { get; set; }
    public string  Code      { get; set; } = "";   // e.g. "LAB-MASON"
    public string  Name      { get; set; } = "";   // e.g. "Mason"
    public string  Unit      { get; set; } = "hr";
    public decimal RatePerHour { get; set; }         // numeric(18,4)
    public bool    IsActive  { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Material resource with a supplier unit price and a wastage allowance
/// (e.g. 0.05 = +5%). Effective consumption = qty × (1 + wastage).
/// </summary>
public class MaterialResource : IHasTenant
{
    public int     Id         { get; set; }
    public Guid    TenantId   { get; set; }
    public string  Code       { get; set; } = "";   // e.g. "MAT-C30"
    public string  Name       { get; set; } = "";   // e.g. "Concrete Grade 30"
    public string  Unit       { get; set; } = "";   // m3, kg, no …
    public decimal UnitPrice  { get; set; }          // numeric(18,4)
    public decimal WastagePct { get; set; }          // e.g. 5.00 (%)
    public string? Supplier   { get; set; }
    public bool    IsActive   { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Equipment / plant resource. Rate is cost per hour (operator/fuel folded in
/// or modelled as separate resources, per estimator preference).
/// </summary>
public class EquipmentResource : IHasTenant
{
    public int     Id          { get; set; }
    public Guid    TenantId    { get; set; }
    public string  Code        { get; set; } = "";   // e.g. "EQP-MIXER"
    public string  Name        { get; set; } = "";   // e.g. "Concrete Mixer"
    public string  Unit        { get; set; } = "hr";
    public decimal RatePerHour { get; set; }          // numeric(18,4)
    public bool    IsActive    { get; set; } = true;
    public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Subcontractor lump/unit rate (e.g. NDT, painting). Priced as a unit cost.
/// </summary>
public class Subcontractor : IHasTenant
{
    public int     Id        { get; set; }
    public Guid    TenantId  { get; set; }
    public string  Code      { get; set; } = "";   // e.g. "SUB-NDT"
    public string  Name      { get; set; } = "";
    public string  Unit      { get; set; } = "";
    public decimal UnitRate  { get; set; }          // numeric(18,4)
    public bool    IsActive  { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
