namespace BidBuilder.Api.Models;

/// <summary>
/// A feature module that can be enabled/disabled per tenant and assigned to
/// teams via <see cref="GroupModule"/>. Codes for BidBuilder:
/// projects · resource-library · assemblies · boq · rate-analysis ·
/// prelims-markups · reports · estimate-admin.
/// </summary>
public class Module : IHasTenant
{
    public int     Id          { get; set; }
    public Guid    TenantId    { get; set; }
    public string  Code        { get; set; } = "";   // e.g. "boq", "rate-analysis"
    public string  Name        { get; set; } = "";   // e.g. "Bill of Quantities"
    public string? Description { get; set; }
    public int     SortOrder   { get; set; }
    public bool    IsActive    { get; set; } = true;
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public ICollection<GroupModule> GroupModules { get; set; } = new List<GroupModule>();
}
