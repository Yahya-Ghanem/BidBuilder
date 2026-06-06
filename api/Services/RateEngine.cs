using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// The unit-rate build-up engine. Resolves an assembly component to a money cost:
///   • Labor / Equipment : factor × rate-per-hour
///   • Material           : factor × unit-price × (1 + wastage%)
///   • Subcontractor      : factor × unit-rate
/// Summing the components yields the assembly's unit rate. All resource lookups
/// go through the tenant-scoped query filter (FirstOrDefault, not Find).
/// </summary>
public class RateEngine(AppDbContext db)
{
    public record Resolved(decimal Cost, decimal ResourceRate, string ResourceCode, string ResourceName);

    private static decimal R(decimal d) => Math.Round(d, 4, MidpointRounding.AwayFromZero);

    /// <summary>Resolve one component to its cost + the underlying resource's
    /// display info. Returns null if the referenced resource no longer exists.
    /// When <paramref name="pricingDate"/> is supplied, the rate is looked up in
    /// <see cref="ResourceRateHistory"/> (most recent row with EffectiveFrom ≤ date,
    /// falling back to the live rate if no history applies).</summary>
    public async Task<Resolved?> ResolveAsync(ResourceType type, int resourceId, decimal factor, DateOnly? pricingDate = null)
    {
        switch (type)
        {
            case ResourceType.Labor:
                var l = await db.LaborResources.FirstOrDefaultAsync(r => r.Id == resourceId);
                if (l is null) return null;
                var lrate = await ResolveRateAsync(type, resourceId, l.RatePerHour, pricingDate);
                return new(R(factor * lrate), lrate, l.Code, l.Name);
            case ResourceType.Equipment:
                var e = await db.EquipmentResources.FirstOrDefaultAsync(r => r.Id == resourceId);
                if (e is null) return null;
                var erate = await ResolveRateAsync(type, resourceId, e.RatePerHour, pricingDate);
                return new(R(factor * erate), erate, e.Code, e.Name);
            case ResourceType.Material:
                var m = await db.MaterialResources.FirstOrDefaultAsync(r => r.Id == resourceId);
                if (m is null) return null;
                // Pricing-date affects the bare unit price; wastage stays current (it's a
                // consumption allowance, not a rate). The displayed ResourceRate is the
                // pre-wastage unit price (consistent with the live path).
                var mrate = await ResolveRateAsync(type, resourceId, m.UnitPrice, pricingDate);
                return new(R(factor * mrate * (1 + m.WastagePct / 100m)), mrate, m.Code, m.Name);
            case ResourceType.Subcontractor:
                var s = await db.Subcontractors.FirstOrDefaultAsync(r => r.Id == resourceId);
                if (s is null) return null;
                var srate = await ResolveRateAsync(type, resourceId, s.UnitRate, pricingDate);
                return new(R(factor * srate), srate, s.Code, s.Name);
            default:
                return null;
        }
    }

    /// <summary>Look up the rate as-at <paramref name="pricingDate"/> from
    /// <see cref="ResourceRateHistory"/>: most recent EffectiveFrom ≤ date wins;
    /// no matching row → fall back to the live <paramref name="liveRate"/>. When
    /// pricingDate is null the live rate is returned unchanged.</summary>
    public async Task<decimal> ResolveRateAsync(ResourceType type, int resourceId, decimal liveRate, DateOnly? pricingDate)
    {
        if (pricingDate is null) return liveRate;
        var snap = await db.ResourceRateHistory
            .Where(h => h.ResourceType == type && h.ResourceId == resourceId && h.EffectiveFrom <= pricingDate.Value)
            .OrderByDescending(h => h.EffectiveFrom)
            .ThenByDescending(h => h.Id)
            .Select(h => (decimal?)h.Rate)
            .FirstOrDefaultAsync();
        return snap ?? liveRate;
    }

    /// <summary>True if the resource referenced by (type, id) exists in this tenant.</summary>
    public async Task<bool> ResourceExistsAsync(ResourceType type, int resourceId) => type switch
    {
        ResourceType.Labor         => await db.LaborResources.AnyAsync(r => r.Id == resourceId),
        ResourceType.Equipment     => await db.EquipmentResources.AnyAsync(r => r.Id == resourceId),
        ResourceType.Material       => await db.MaterialResources.AnyAsync(r => r.Id == resourceId),
        ResourceType.Subcontractor => await db.Subcontractors.AnyAsync(r => r.Id == resourceId),
        _ => false,
    };

    /// <summary>Recompute and persist an assembly's unit rate from its components.</summary>
    public async Task<decimal> RecomputeAssemblyAsync(int assemblyId)
    {
        var a = await db.Assemblies.Include(x => x.Components).FirstOrDefaultAsync(x => x.Id == assemblyId);
        if (a is null) return 0m;

        decimal total = 0m;
        foreach (var c in a.Components)
        {
            var resolved = await ResolveAsync(c.ResourceType, c.ResourceId, c.Factor);
            if (resolved is not null) total += resolved.Cost;
        }

        a.ComputedRate = R(total);
        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return a.ComputedRate;
    }

    /// <summary>
    /// Compute an assembly's unit rate as it WOULD be at a given pricing date,
    /// WITHOUT persisting (the live <see cref="Assembly.ComputedRate"/> cache stays at
    /// the live rate). Used by <see cref="EstimateCalculator"/> when an estimate has a
    /// pricing-date set, so its bid reflects historical supplier prices without
    /// disturbing the shared library.
    /// </summary>
    public async Task<decimal> ComputeAssemblyRateAsAtAsync(int assemblyId, DateOnly pricingDate)
    {
        var a = await db.Assemblies.AsNoTracking().Include(x => x.Components)
            .FirstOrDefaultAsync(x => x.Id == assemblyId);
        if (a is null) return 0m;

        decimal total = 0m;
        foreach (var c in a.Components)
        {
            var resolved = await ResolveAsync(c.ResourceType, c.ResourceId, c.Factor, pricingDate);
            if (resolved is not null) total += resolved.Cost;
        }
        return R(total);
    }
}
