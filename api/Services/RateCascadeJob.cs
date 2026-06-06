using BidBuilder.Api.Models;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Services;

/// <summary>
/// The Hangfire-invoked entry point for the rate-cascade fan-out. Hangfire creates
/// a fresh DI scope per job invocation, so this class resolves a brand-new
/// <see cref="ITenantContext"/>, <see cref="RateCascadeService"/>, and (transitively)
/// <see cref="Data.AppDbContext"/>. The job's payload carries the tenant explicitly
/// — outside a request, no middleware ran to resolve one.
///
/// Public methods used by Hangfire must be non-static; arguments must be JSON-serializable.
/// </summary>
public sealed class RateCascadeJob(ITenantContext tenant, RateCascadeService cascade, ILogger<RateCascadeJob> log)
{
    /// <summary>
    /// Set the tenant on the scoped <see cref="ITenantContext"/> so EF's query filter
    /// scopes correctly, then run the cascade. Idempotent — Hangfire may invoke this
    /// more than once on retry, and the cascade only writes a fixed-point recompute.
    /// </summary>
    public async Task RunAsync(Guid tenantId, ResourceType type, int resourceId)
    {
        // The tenant slug is informational on the request side (logging, headers); the
        // cascade only reads TenantId via the query filter, so a placeholder is fine.
        tenant.Set(tenantId, "<background-job>");
        var (assemblies, estimates) = await cascade.OnResourceChangedAsync(type, resourceId);
        log.LogInformation(
            "Cascade for tenant {Tenant} {Type}#{ResourceId} touched {Assemblies} assembly(ies) and {Estimates} estimate(s).",
            tenantId, type, resourceId, assemblies, estimates);
    }
}
