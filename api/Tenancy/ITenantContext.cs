namespace BidBuilder.Api.Tenancy;

/// <summary>
/// Holds the tenant for the current request. Injected as a scoped service so
/// the DbContext can read it inside its query filter and SaveChanges hook.
///
/// Populated by <see cref="TenantResolutionMiddleware"/> before any endpoint
/// or DbContext is touched.
/// </summary>
public interface ITenantContext
{
    /// <summary>Slug of the current tenant ("default", "acme", …).</summary>
    string TenantSlug { get; }

    /// <summary>Resolved tenant ID. Throws if not set — middleware ran out of order.</summary>
    Guid TenantId { get; }

    /// <summary>True after the middleware has resolved a tenant for this request.</summary>
    bool IsResolved { get; }

    void Set(Guid tenantId, string tenantSlug);
}

internal sealed class TenantContext : ITenantContext
{
    private Guid?   _id;
    private string? _slug;

    public string TenantSlug => _slug
        ?? throw new InvalidOperationException("Tenant not resolved for this request");

    public Guid TenantId => _id
        ?? throw new InvalidOperationException("Tenant not resolved for this request");

    public bool IsResolved => _id.HasValue;

    public void Set(Guid tenantId, string tenantSlug)
    {
        if (_id.HasValue) throw new InvalidOperationException("TenantContext is set-once per request");
        _id   = tenantId;
        _slug = tenantSlug;
    }
}
