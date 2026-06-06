namespace BidBuilder.Api.Models;

/// <summary>
/// A dated rate point for a library resource — the "what did labor cost on Jan 14?"
/// audit trail. One row per (resource, effective-from) snapshot; the engine resolves
/// a rate as-at a date by picking the most recent row whose <see cref="EffectiveFrom"/>
/// is ≤ that date (and falling back to the live resource rate if none).
///
/// Rows are auto-recorded when a resource's rate changes via the API (the prior rate
/// is snapshotted with the change timestamp) and may also be inserted manually to
/// back-date a known supplier change. <see cref="Source"/> is free-text (a supplier
/// PO ref, a quote number, "manual back-date by Y. Ghanem", etc.).
/// </summary>
public class ResourceRateHistory : IHasTenant
{
    public int          Id            { get; set; }
    public Guid         TenantId      { get; set; }
    public ResourceType ResourceType  { get; set; }
    public int          ResourceId    { get; set; }
    /// <summary>The date from which this rate applies. A pricing-date lookup picks
    /// the most recent row with EffectiveFrom ≤ pricingDate.</summary>
    public DateOnly     EffectiveFrom { get; set; }
    /// <summary>The rate value (per-hour for Labor/Equipment, unit price for Material,
    /// unit rate for Subcontractor). For Material this is the bare unit price BEFORE
    /// wastage — wastage is applied by the engine, same as the live resource.</summary>
    public decimal      Rate          { get; set; }   // numeric(18,4)
    /// <summary>Free-text provenance: supplier ref, quote number, manual note.</summary>
    public string?      Source        { get; set; }
    public DateTime     CreatedAt     { get; set; } = DateTime.UtcNow;
}
