namespace BidBuilder.Api.Models;

/// <summary>
/// A team within a tenant. A Group bundles a set of module permissions (via
/// <see cref="GroupModule"/>), has member users (via <see cref="UserGroup"/>),
/// and is granted access to specific projects (via <see cref="ProjectTeam"/>).
/// Tenant-scoped.
/// </summary>
public class Group : IHasTenant
{
    public int     Id          { get; set; }
    public Guid    TenantId    { get; set; }
    public string  Code        { get; set; } = "";   // e.g. "TEAM-001" — unique within tenant
    public string  Name        { get; set; } = "";   // e.g. "Estimating Team A"
    public string? Description { get; set; }
    public int     MemberCount { get; set; }
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;

    /// <summary>Built-in groups (e.g. per-tenant "Admins") cannot be deleted.</summary>
    public bool IsBuiltIn { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public ICollection<GroupModule> GroupModules { get; set; } = new List<GroupModule>();
    public ICollection<UserGroup>   UserGroups   { get; set; } = new List<UserGroup>();
    public ICollection<ProjectTeam> ProjectTeams { get; set; } = new List<ProjectTeam>();
}
