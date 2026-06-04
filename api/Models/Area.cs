namespace BidBuilder.Api.Models;

/// <summary>
/// A node in a project's physical/location breakdown structure (Area → Sub-area →
/// Unit …), nesting arbitrarily via <see cref="ParentAreaId"/>. Areas live on the
/// project and are shared across all estimate revisions; BOQ items are tagged to an
/// area so their line totals roll up unit → sub-area → area → project per estimate.
/// </summary>
public class Area : IHasTenant
{
    public int      Id           { get; set; }
    public Guid     TenantId     { get; set; }
    public int      ProjectId    { get; set; }
    public int?     ParentAreaId { get; set; }   // null = top-level

    public string   Name         { get; set; } = "";
    public string?  Code         { get; set; }
    public AreaKind Kind         { get; set; } = AreaKind.Area;
    public int      SortOrder    { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Project Project { get; set; } = null!;
}
