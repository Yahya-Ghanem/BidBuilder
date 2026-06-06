namespace BidBuilder.Api.Models;

/// <summary>
/// A defensible contingency item on an estimate's risk register (19.2). Each row
/// names a risk, a probability of it occurring (0–100%), and an impact in the
/// estimate's currency — the expected value (EV = probability/100 × impact) is
/// summed across the register to PRODUCE a suggested contingency figure the
/// estimator can apply to the existing Contingency markup.
///
/// This DOES NOT mutate the bid on its own — the estimator chooses whether to
/// accept the suggestion. Without explicit application, the register is just a
/// reasoning aid + reviewer-visible audit trail.
/// </summary>
public class RiskItem : IHasTenant
{
    public int          Id          { get; set; }
    public Guid         TenantId    { get; set; }
    public int          EstimateId  { get; set; }

    public string       Title       { get; set; } = "";
    /// <summary>Risk category (Schedule, Cost, Design, Technical, External, Other).
    /// Stored as a string for forward-compatibility — the catalog can extend
    /// without an EF migration.</summary>
    public RiskCategory Category    { get; set; } = RiskCategory.Other;
    /// <summary>Probability % the risk materialises. 0–100. numeric(9,4).</summary>
    public decimal      ProbabilityPct { get; set; }
    /// <summary>Cost impact if the risk does materialise (estimate currency, 2dp).</summary>
    public decimal      ImpactAmount   { get; set; }
    public string?      Note        { get; set; }
    public int          SortOrder   { get; set; }

    public DateTime     CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime     UpdatedAt   { get; set; } = DateTime.UtcNow;
}

/// <summary>Catalog of risk categories — labels for grouping; the engine
/// treats them all the same when computing EV.</summary>
public enum RiskCategory
{
    Schedule   = 0,
    Cost       = 1,
    Design     = 2,
    Technical  = 3,
    External   = 4,
    Other      = 99,
}
