using System.Diagnostics;
using BidBuilder.Api.Models;
using BidBuilder.Api.Telemetry;
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
        // 19.3 — own ActivitySource so the cascade is its own trace span (instead of
        // a bare EF span graph) and so cascade.runs/.failures counters carry the
        // tenant/resource as metric tags for slicing.
        using var activity = BidBuilderTelemetry.ActivitySource.StartActivity("cascade.run");
        activity?.SetTag("tenant.id", tenantId);
        activity?.SetTag("resource.type", type.ToString());
        activity?.SetTag("resource.id", resourceId);
        var tags = new TagList
        {
            { "resource.type", type.ToString() },
        };
        try
        {
            var (assemblies, estimates) = await cascade.OnResourceChangedAsync(type, resourceId);
            activity?.SetTag("cascade.assemblies", assemblies);
            activity?.SetTag("cascade.estimates", estimates);
            BidBuilderTelemetry.CascadeRuns.Add(1, tags);
            log.LogInformation(
                "Cascade for tenant {Tenant} {Type}#{ResourceId} touched {Assemblies} assembly(ies) and {Estimates} estimate(s).",
                tenantId, type, resourceId, assemblies, estimates);
        }
        catch (Exception ex)
        {
            BidBuilderTelemetry.CascadeFailures.Add(1, tags);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
