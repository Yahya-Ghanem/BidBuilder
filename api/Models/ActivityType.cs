namespace BidBuilder.Api.Models;

/// <summary>
/// A tenant catalog of construction activities (e.g. Excavation, Block work,
/// Plastering, Tiling…) offered as a dropdown when adding an activity under a unit.
/// Common activities are seeded as built-ins (not deletable); tenants add their own.
/// The catalog only supplies the activity name — picking one fills the BOQ line's
/// description, so there is no hard reference from priced items back to the catalog.
/// </summary>
public class ActivityType : IHasTenant
{
    public int      Id        { get; set; }
    public Guid     TenantId  { get; set; }
    public string   Name      { get; set; } = "";   // unique within tenant
    public int      SortOrder { get; set; }
    public bool     IsActive  { get; set; } = true;
    public bool     Builtin   { get; set; }          // seeded defaults — cannot be deleted
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
