namespace BidBuilder.Api.Models;

/// <summary>
/// A tenant catalog of project types (e.g. Civil, Mechanical, Electrical, Plumbing…)
/// offered as a dropdown when creating or editing a project. Common types are seeded
/// as built-ins (not deletable); tenants add their own and the list can grow.
/// A project references one by <see cref="Project.ProjectTypeId"/> (nullable — a
/// project need not be typed).
/// </summary>
public class ProjectType : IHasTenant
{
    public int      Id        { get; set; }
    public Guid     TenantId  { get; set; }
    public string   Name      { get; set; } = "";   // unique within tenant
    public int      SortOrder { get; set; }
    public bool     IsActive  { get; set; } = true;
    public bool     Builtin   { get; set; }          // seeded defaults — cannot be deleted
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
