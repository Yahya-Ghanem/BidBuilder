using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// 29.B.1 — Resolves whether a feature is enabled for a given tenant. Read by
/// the API to gate endpoints, and serialized to <c>/api/me/features</c> so the
/// SPA can hide UI surfaces server-side.
/// </summary>
public interface IFeatureService
{
    /// <summary>
    /// Resolve a single flag for the given tenant. Unknown keys resolve to false
    /// so a typo in application code defaults to "feature off" rather than
    /// accidentally exposing something.
    /// </summary>
    Task<bool> IsEnabledAsync(string key, Guid tenantId, CancellationToken ct = default);

    /// <summary>Resolve every flag for the given tenant in one shot.</summary>
    Task<IReadOnlyDictionary<string, bool>> ResolveAllAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>List the raw flag catalogue (admin endpoint).</summary>
    Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Invalidate the in-memory cache. Called after every admin write so a flag
    /// flip propagates immediately instead of waiting for the 30-second TTL.
    /// The 30s TTL is the safety net for stale replicas / horizontal scale; the
    /// invalidation is the fast path that hits the <c>&lt; 60s</c> acceptance.
    /// </summary>
    void Invalidate();
}

public sealed class FeatureService : IFeatureService
{
    // The whole flag catalogue is small (tens of rows at most) and read on
    // every request that gates anything — caching the entire set is simpler
    // than per-key entries and avoids cache-stampede edge cases.
    private const string CacheKey = "feature-flags-catalogue";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public FeatureService(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<bool> IsEnabledAsync(string key, Guid tenantId, CancellationToken ct = default)
    {
        var tenantSlug = await SlugForAsync(tenantId, ct);
        var flags = await LoadFlagsAsync(ct);
        var flag = flags.FirstOrDefault(f => f.Key == key);
        if (flag is null) return false;
        return ResolveFor(flag, tenantId, tenantSlug);
    }

    public async Task<IReadOnlyDictionary<string, bool>> ResolveAllAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenantSlug = await SlugForAsync(tenantId, ct);
        var flags = await LoadFlagsAsync(ct);
        var result = new Dictionary<string, bool>(flags.Count);
        foreach (var f in flags) result[f.Key] = ResolveFor(f, tenantId, tenantSlug);
        return result;
    }

    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default)
        => await LoadFlagsAsync(ct);

    public void Invalidate() => _cache.Remove(CacheKey);

    private async Task<List<FeatureFlag>> LoadFlagsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue<List<FeatureFlag>>(CacheKey, out var cached) && cached is not null)
            return cached;
        // AsNoTracking — the cached list is read-only at this layer; admin writes
        // go through a fresh tracked query in the endpoint.
        var list = await _db.FeatureFlags.AsNoTracking().ToListAsync(ct);
        _cache.Set(CacheKey, list, CacheTtl);
        return list;
    }

    private async Task<string> SlugForAsync(Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters because Tenant has the slug we need even from a
        // SuperAdmin context where _tenant.TenantId would be empty.
        return await _db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(ct) ?? string.Empty;
    }

    /// <summary>
    /// The resolver. See the docstring on <see cref="FeatureFlag"/> for the
    /// precedence rules.
    /// </summary>
    public static bool ResolveFor(FeatureFlag flag, Guid tenantId, string tenantSlug)
    {
        // 1. Explicit per-tenant override wins over everything, including the
        //    master kill switch. That's the "force-on for a debug tenant when
        //    the feature is globally off" escape hatch.
        if (!string.IsNullOrEmpty(tenantSlug) && flag.Overrides.TryGetValue(tenantSlug, out var forced))
            return forced;

        // 2. Master kill switch.
        if (!flag.Enabled) return false;

        // 3. 100 % = always on, 0 % = always off (degenerate cases of the hash
        //    bucket — pulled out so a fresh-deployment default of 100 doesn't
        //    incur a needless hash even once).
        if (flag.RolloutPercentage >= 100) return true;
        if (flag.RolloutPercentage <= 0) return false;

        // 4. Deterministic per-tenant bucket. Stable across requests so a
        //    tenant doesn't see the feature flicker on and off between calls.
        var bucket = StableBucket(flag.Key, tenantId);
        return bucket < flag.RolloutPercentage;
    }

    /// <summary>
    /// Map <c>(flag key, tenant id)</c> to a stable integer in [0, 100).
    /// SHA-256 over <c>"key:guid"</c> → take 4 bytes → mod 100. Cryptographic
    /// hash is overkill for "uniform distribution across a percentage" but the
    /// cost is negligible compared to the DB roundtrip and the determinism
    /// guarantee is what matters.
    /// </summary>
    private static int StableBucket(string key, Guid tenantId)
    {
        var seed = $"{key}:{tenantId:N}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var n = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return (int)(n % 100);
    }
}
