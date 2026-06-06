namespace BidBuilder.Api.Models;

/// <summary>
/// A tenant — a contracting company using BidBuilder. Every tenant-owned table
/// (Project, Estimate, resource library …) references this via a TenantId column
/// and is auto-scoped by the global query filter in <c>AppDbContext</c>.
///
/// <c>Slug</c> is the stable URL/header-friendly identifier (e.g. "acme-build").
/// <c>Id</c> is the FK target — internal only, never exposed in URLs.
/// </summary>
public class Tenant
{
    public Guid     Id            { get; set; }
    public string   Slug          { get; set; } = "";   // url-safe, unique
    public string   Name          { get; set; } = "";   // display name
    public string?  BrandColor    { get; set; }          // hex (#RRGGBB)
    public string?  LogoUrl       { get; set; }

    /// <summary>20.11 — optional vanity host (e.g. "bids.acme.com"). When a request
    /// arrives with no auth token and no X-Tenant-Id header, the tenant is resolved
    /// by matching this against the request Host. Stored lowercase; unique per tenant.</summary>
    public string?  CustomDomain  { get; set; }

    public string   DefaultLocale { get; set; } = "en";  // "en" | "ar"
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;

    /// <summary>When true, /auth/login refuses sign-ins for users in this tenant.</summary>
    public bool IsSuspended { get; set; }
}

/// <summary>
/// Marker for tenant-owned entities. Adding <c>: IHasTenant</c> to a model makes
/// it pick up the global query filter and the auto-stamping behaviour in AppDbContext.
/// </summary>
public interface IHasTenant
{
    Guid TenantId { get; set; }
}
