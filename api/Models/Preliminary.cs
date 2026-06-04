namespace BidBuilder.Api.Models;

/// <summary>
/// A preliminary / site-overhead (indirect) cost line on an estimate —
/// supervision, temporary facilities, mobilization, insurance, scaffolding.
/// <see cref="PreliminaryKind.TimeRelated"/> lines multiply by the project
/// duration (months); <see cref="PreliminaryKind.Fixed"/> lines are one-off.
/// </summary>
public class Preliminary : IHasTenant
{
    public int             Id         { get; set; }
    public Guid            TenantId   { get; set; }
    public int             EstimateId { get; set; }

    public string          Description { get; set; } = "";
    public PreliminaryKind Kind        { get; set; } = PreliminaryKind.Fixed;

    /// <summary>For Fixed: the total amount. For TimeRelated: the amount PER MONTH
    /// (engine multiplies by Project.DurationMonths). numeric(18,2).</summary>
    public decimal         Amount      { get; set; }

    /// <summary>Cached resolved total used in the roll-up (numeric(18,2)).</summary>
    public decimal         ComputedTotal { get; set; }

    public int             SortOrder   { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Estimate Estimate { get; set; } = null!;
}
