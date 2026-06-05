namespace BidBuilder.Api.Models;

/// <summary>
/// A bid opportunity / tender. A tenant owns many, run concurrently. Teams
/// (<see cref="Group"/>) are granted access via <see cref="ProjectTeam"/>, and
/// each project holds one or more <see cref="Estimate"/> revisions.
/// </summary>
public class Project : IHasTenant
{
    public int           Id          { get; set; }
    public Guid          TenantId    { get; set; }
    public string        Code        { get; set; } = "";   // e.g. "PRJ-2026-001" — unique within tenant
    public string        Name        { get; set; } = "";
    public string?       ClientName  { get; set; }
    public string?       Location    { get; set; }
    public string        Currency    { get; set; } = "AED"; // ISO-4217
    public ProjectStatus Status      { get; set; } = ProjectStatus.Draft;
    public int?          ProjectTypeId { get; set; }         // → ProjectType catalog (nullable)

    public DateTime?     TenderDueAt { get; set; }
    public int?          DurationMonths { get; set; }       // drives time-related prelims

    public DateTime      CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime      UpdatedAt   { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public ICollection<ProjectTeam> ProjectTeams { get; set; } = new List<ProjectTeam>();
    public ICollection<Estimate>    Estimates    { get; set; } = new List<Estimate>();
}
