namespace BidBuilder.Api.Models;

/// <summary>
/// Many-to-many join between <see cref="Project"/> and <see cref="Group"/> (team).
/// Existence of a row grants the team access to the project — "every team can
/// log in to their project." What members may do inside is still governed by the
/// team's <see cref="GroupModule"/> permissions. TenantId denormalised for filtering.
/// </summary>
public class ProjectTeam : IHasTenant
{
    public int  ProjectId { get; set; }
    public int  GroupId   { get; set; }
    public Guid TenantId  { get; set; }

    /// <summary>The team that "owns" the lead estimate gets the primary flag.</summary>
    public bool IsLead { get; set; }

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Project Project { get; set; } = null!;
    public Group   Group   { get; set; } = null!;
}
