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

    // ── Bid outcome register (19.1) ───────────────────────────────────────────
    // The "as-bid vs awarded vs actual" learning loop. None of these are required;
    // a fresh project has them all null. They feed the analytics dashboard.
    // BidPrice on a Published estimate is the SOURCE for "as-bid"; capturing
    // SubmittedBidValue here lets the user record the actual number that went out
    // on letterhead (which can differ from any single revision, e.g. a final
    // discount). Currency is always the project Currency — no FX in these fields.

    /// <summary>The bid amount actually submitted to the client (the figure on the
    /// covering letter / formal tender). numeric(18,2). Null until set.</summary>
    public decimal?      SubmittedBidValue { get; set; }
    /// <summary>The awarded contract value when ProjectStatus = Won. numeric(18,2).</summary>
    public decimal?      AwardedValue { get; set; }
    /// <summary>Final settled project cost — optional, captured after delivery for
    /// the cost-variance/learning loop. numeric(18,2).</summary>
    public decimal?      FinalCost { get; set; }
    /// <summary>When the win/loss was decided (used as the period axis on the dashboard).</summary>
    public DateTime?     DecisionAt { get; set; }
    /// <summary>Free-text note explaining the loss/win — e.g. "lost on price by 4%".</summary>
    public string?       WinLossNote { get; set; }

    public DateTime      CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime      UpdatedAt   { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────────
    public ICollection<ProjectTeam> ProjectTeams { get; set; } = new List<ProjectTeam>();
    public ICollection<Estimate>    Estimates    { get; set; } = new List<Estimate>();
}
