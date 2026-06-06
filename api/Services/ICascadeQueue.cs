using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// Decouples the HTTP request that mutates a library resource from the
/// fan-out that recomputes every dependent assembly + estimate.
///
/// Resource edits are common, but the cascade is expensive (it can touch many
/// estimates per tenant). Pushing it into a background job keeps the PUT/DELETE
/// response fast and predictable — the user sees the rate change save instantly,
/// and the recompute runs in the background where a slow retry doesn't hurt the UX.
///
/// Two implementations:
///   • <see cref="HangfireCascadeQueue"/> — persists the work to the Postgres-backed
///     Hangfire queue, processed by an in-process worker. Used in production.
///   • <see cref="InlineCascadeQueue"/> — runs the cascade synchronously inside the
///     request. Used in tests (so assertions can read the cascaded effect immediately)
///     and as a fallback when background jobs are disabled by config.
///
/// The tenant the work belongs to MUST be carried in the payload — the job runs
/// in a fresh DI scope where <c>ITenantContext</c> starts unresolved, and EF's
/// query filter needs a resolved tenant to scope correctly.
/// </summary>
public interface ICascadeQueue
{
    /// <summary>Schedule (or run inline) a cascade for a resource change in the
    /// given tenant. Returns when the work is enqueued, NOT when it has run —
    /// callers must not assume the cascade is complete.</summary>
    Task EnqueueResourceChangedAsync(Guid tenantId, ResourceType type, int resourceId);
}
