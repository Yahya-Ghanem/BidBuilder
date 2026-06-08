using System.Collections.Concurrent;

namespace BidBuilder.Api.Services;

/// <summary>
/// 27.2 — In-process "who's viewing this revision" store. The estimate editor
/// posts a heartbeat every 30s; this store records the observation and silently
/// prunes entries older than its TTL on every Touch/List so the dictionary
/// never grows without bound.
///
/// Singleton — the bucket map must be shared across requests. NOT persisted:
/// on a process restart everyone's seen-state resets and the heartbeat refills
/// it within a poll cycle. Acceptable because presence is a soft cue, not bid
/// state. Multi-replica deployments would need a shared store (Redis) — out of
/// scope for the dev stack.
///
/// Tenant isolation is enforced by the route, not the store: the heartbeat
/// endpoint goes through Guard(boq+View) + CanAccessEstimateAsync, so cross-
/// tenant probes 404 before they can touch the bucket. Two tenants whose
/// estimate ids happen to collide can't anyway — estimate ids are tenant-
/// scoped sequences, and the access guard would have rejected the wrong one.
///
/// Thread-safety: ConcurrentDictionary for the outer map + per-bucket
/// ConcurrentDictionary for entries. The pruning pass enumerates the bucket
/// and removes by key — both reader-safe under ConcurrentDictionary's
/// snapshot semantics.
/// </summary>
public sealed class PresenceStore
{
    /// <summary>One user's last observation on one revision.</summary>
    /// <param name="UserId">Stable user id from the JWT sub claim.</param>
    /// <param name="Name">Display name (may be empty if the user has no name set).</param>
    /// <param name="Email">Email — used as a fallback display label and for avatar initials.</param>
    /// <param name="LastSeenUtc">Server timestamp of the most recent heartbeat or initial GET.</param>
    public sealed record Entry(int UserId, string Name, string Email, DateTime LastSeenUtc);

    /// <summary>Entries older than this are pruned on every Touch/List. 90 s covers a
    /// client poll cadence of 30 s with 2× safety for a single skipped beat + network jitter.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, Entry>> _buckets = new();

    /// <summary>Record a heartbeat and return the current live list (post-prune).</summary>
    public IReadOnlyList<Entry> Touch(int estimateId, int userId, string name, string email, DateTime nowUtc)
    {
        var bucket = _buckets.GetOrAdd(estimateId, _ => new ConcurrentDictionary<int, Entry>());
        bucket[userId] = new Entry(userId, name ?? string.Empty, email ?? string.Empty, nowUtc);
        return PruneAndSnapshot(bucket, nowUtc);
    }

    /// <summary>Return the current live list without recording a heartbeat. Stale
    /// entries are still pruned — keeps the bucket from accumulating ghosts when only
    /// reads happen (e.g. a passive observer page that never heartbeats).</summary>
    public IReadOnlyList<Entry> List(int estimateId, DateTime nowUtc)
    {
        if (!_buckets.TryGetValue(estimateId, out var bucket)) return Array.Empty<Entry>();
        return PruneAndSnapshot(bucket, nowUtc);
    }

    private static IReadOnlyList<Entry> PruneAndSnapshot(ConcurrentDictionary<int, Entry> bucket, DateTime nowUtc)
    {
        var cutoff = nowUtc - Ttl;
        // Two-pass: collect stale keys first, then remove. Removing during enumeration
        // is safe with ConcurrentDictionary but the two-pass keeps the intent obvious.
        var stale = bucket.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList();
        foreach (var k in stale) bucket.TryRemove(k, out _);
        // Most-recent-first so the UI avatar cluster shows the freshest viewers up front.
        return bucket.Values.OrderByDescending(v => v.LastSeenUtc).ToList();
    }
}
