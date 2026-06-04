namespace BidBuilder.Api.Models;

/// <summary>
/// A reusable unit-rate build-up (rate analysis / "recipe"). E.g. "Reinforced
/// concrete footing, per m³" = labor + material + equipment components. Computing
/// the components yields the assembly's unit rate, reusable across BOQ items and
/// projects. Tenant-scoped.
/// </summary>
public class Assembly : IHasTenant
{
    public int     Id          { get; set; }
    public Guid    TenantId    { get; set; }
    public string  Code        { get; set; } = "";   // e.g. "ASM-RC-FOOT"
    public string  Name        { get; set; } = "";
    public string  Unit        { get; set; } = "";   // the unit the rate is expressed per
    public bool    IsActive    { get; set; } = true;

    /// <summary>Cached computed unit rate (sum of components; numeric(18,4)).</summary>
    public decimal ComputedRate { get; set; }

    public DateTime UpdatedAt   { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public ICollection<AssemblyComponent> Components { get; set; } = new List<AssemblyComponent>();
}

/// <summary>
/// One line in an assembly's build-up: a reference to a resource (by type + id)
/// and the consumption factor — how much of that resource is needed per one unit
/// of the assembly. Component cost = factor × resource rate (× wastage for
/// materials). Storing the resolved unit cost is left to the engine.
/// </summary>
public class AssemblyComponent : IHasTenant
{
    public int          Id          { get; set; }
    public Guid         TenantId    { get; set; }
    public int          AssemblyId  { get; set; }

    public ResourceType ResourceType { get; set; }
    public int          ResourceId   { get; set; }   // FK into the matching resource table (by type)

    /// <summary>Quantity of the resource consumed per one unit of the assembly
    /// (e.g. 1.2 mason-hours per m³). numeric(18,4).</summary>
    public decimal      Factor       { get; set; }

    public string?      Note         { get; set; }
    public int          SortOrder    { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Assembly Assembly { get; set; } = null!;
}
