namespace BidBuilder.Api.Models;

/// <summary>
/// Many-to-many join between <see cref="Group"/> and <see cref="Module"/>.
/// Existence of a row means the team has access to the module; the flags say
/// what they may do. TenantId is denormalised for direct query filtering.
/// </summary>
public class GroupModule : IHasTenant
{
    public int  GroupId  { get; set; }
    public int  ModuleId { get; set; }
    public Guid TenantId { get; set; }

    // ── Permission flags ───────────────────────────────────────────────────────
    public bool CanView   { get; set; } = true;
    public bool CanAdd    { get; set; }
    public bool CanEdit   { get; set; }
    public bool CanDelete { get; set; }

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Group  Group  { get; set; } = null!;
    public Module Module { get; set; } = null!;
}
