namespace BidBuilder.Api.Models;

/// <summary>
/// Many-to-many join between <see cref="User"/> and <see cref="Group"/> (team).
/// Existence of a row means the user is a member of the team.
/// TenantId is denormalised for direct query filtering.
/// </summary>
public class UserGroup : IHasTenant
{
    public int  UserId   { get; set; }
    public int  GroupId  { get; set; }
    public Guid TenantId { get; set; }

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public User  User  { get; set; } = null!;
    public Group Group { get; set; } = null!;
}
