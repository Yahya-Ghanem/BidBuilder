using BidBuilder.Api.Models;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Services;

/// <summary>
/// Synchronous in-process implementation of <see cref="ICascadeQueue"/>: runs the
/// cascade right inside the request that triggered it. Used in tests (so a PUT
/// that changes a labor rate is followed by an assertion on the dependent
/// assembly's recomputed unit rate, without polling Hangfire) and when background
/// jobs are disabled by config in dev environments.
///
/// Tenant context is already resolved in the request scope, so the payload
/// <paramref name="tenantId"/> is checked for safety and otherwise unused.
/// </summary>
public sealed class InlineCascadeQueue(RateCascadeService cascade, ITenantContext tenant) : ICascadeQueue
{
    public async Task EnqueueResourceChangedAsync(Guid tenantId, ResourceType type, int resourceId)
    {
        // Safety: the inline path runs inside the same scope as the request, so the
        // resolved tenant MUST match the payload — a mismatch means a caller messed up.
        if (tenant.IsResolved && tenant.TenantId != tenantId)
            throw new InvalidOperationException(
                $"InlineCascadeQueue: payload tenant {tenantId} does not match request tenant {tenant.TenantId}.");
        await cascade.OnResourceChangedAsync(type, resourceId);
    }
}
