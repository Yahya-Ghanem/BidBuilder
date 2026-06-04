namespace BidBuilder.Api.Models;

/// <summary>
/// A markup applied on top of (direct + indirect) cost: overhead, profit,
/// contingency or escalation. Applied as a percentage; markups apply in
/// <see cref="ApplyOrder"/> sequence, each on the running subtotal (compounding),
/// which matches standard tender build-up practice.
/// </summary>
public class Markup : IHasTenant
{
    public int        Id         { get; set; }
    public Guid       TenantId   { get; set; }
    public int        EstimateId { get; set; }

    public MarkupType Type       { get; set; }
    public string?    Label      { get; set; }

    /// <summary>Percentage, e.g. 12.50 = 12.5%. numeric(9,4).</summary>
    public decimal    Percentage { get; set; }

    /// <summary>Order in which this markup is applied to the running subtotal.</summary>
    public int        ApplyOrder { get; set; }

    /// <summary>Cached money added by this markup (numeric(18,2)).</summary>
    public decimal    ComputedAmount { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public Estimate Estimate { get; set; } = null!;
}
