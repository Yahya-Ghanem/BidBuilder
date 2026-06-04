namespace BidBuilder.Api.Models;

/// <summary>
/// A versioned bid for a <see cref="Project"/>. Holds the BOQ tree, preliminaries
/// and markups; rolling these up yields the bid price. Multiple revisions can
/// exist (Draft → Published → Superseded). Optimistic concurrency via Postgres
/// xmin (configured in AppDbContext) protects concurrent team edits.
/// </summary>
public class Estimate : IHasTenant
{
    public int            Id        { get; set; }
    public Guid           TenantId  { get; set; }
    public int            ProjectId { get; set; }

    public int            Revision  { get; set; } = 1;       // 1, 2, 3 … per project
    public string         Title     { get; set; } = "";
    public EstimateStatus Status    { get; set; } = EstimateStatus.Draft;
    public string         Currency  { get; set; } = "AED";

    // ── Presentation currency (FX) ────────────────────────────────────────────
    // The bid is additionally shown converted into SecondaryCurrency. While the
    // estimate is Draft/UnderReview the live tenant rate is used; on Publish the
    // native→secondary factor is snapshotted into FxRate so the figure never drifts.
    public string?        SecondaryCurrency { get; set; }   // null = none
    public decimal?       FxRate            { get; set; }   // frozen native→secondary factor
    public DateTime?      FxRateAt          { get; set; }   // when the rate was frozen

    /// <summary>Default labor cost rate (cost per man-hour) when an assembly
    /// component does not pin its own rate. Tunable per estimate.</summary>
    public decimal        DefaultLaborRate { get; set; }

    // Cached roll-up totals (recomputed by the engine on save; numeric(18,2)).
    public decimal DirectCost      { get; set; }
    public decimal IndirectCost    { get; set; }   // sum of preliminaries
    public decimal MarkupCost      { get; set; }   // sum of applied markups
    public decimal BidPrice        { get; set; }   // final tender sum

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Project                Project       { get; set; } = null!;
    public ICollection<BoqSection> Sections     { get; set; } = new List<BoqSection>();
    public ICollection<Preliminary> Preliminaries { get; set; } = new List<Preliminary>();
    public ICollection<Markup>      Markups      { get; set; } = new List<Markup>();
}
