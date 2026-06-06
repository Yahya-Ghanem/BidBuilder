using Hangfire;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// Hangfire-backed <see cref="ICascadeQueue"/>. Enqueues a cascade as a persisted
/// background job in the Postgres-backed Hangfire queue, where it will be picked up
/// by an in-process <c>BackgroundJobServer</c>. The actual cascade work runs in
/// <see cref="RateCascadeJob.RunAsync"/> inside a fresh DI scope.
///
/// Hangfire serializes job arguments as JSON, so payload types must be JSON-friendly
/// (Guid + enum + int all are).
/// </summary>
public sealed class HangfireCascadeQueue(IBackgroundJobClient jobs) : ICascadeQueue
{
    public Task EnqueueResourceChangedAsync(Guid tenantId, ResourceType type, int resourceId)
    {
        // BackgroundJob.Enqueue captures the call as (typeof(RateCascadeJob), method,
        // args[]). Hangfire later resolves RateCascadeJob from DI in a fresh scope and
        // invokes the method with the deserialized args.
        jobs.Enqueue<RateCascadeJob>(j => j.RunAsync(tenantId, type, resourceId));
        return Task.CompletedTask;
    }
}
