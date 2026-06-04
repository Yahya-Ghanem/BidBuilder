namespace BidBuilder.Api.Models;

/// <summary>
/// Append-only audit trail per tenant. One row per meaningful mutation
/// (estimate published, rate changed, team assigned to project …).
/// </summary>
public class AuditEvent : IHasTenant
{
    public long     Id         { get; set; }
    public Guid     TenantId   { get; set; }
    public DateTime At         { get; set; } = DateTime.UtcNow;

    public string?  ActorEmail { get; set; }
    public string?  ActorName  { get; set; }
    public string?  ActorRole  { get; set; }

    public string   Action     { get; set; } = "";   // e.g. "estimate.publish"
    public string   Entity     { get; set; } = "";   // e.g. "Estimate"
    public string?  EntityKey  { get; set; }          // e.g. estimate id
    public string?  Summary    { get; set; }
}
