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
    /// display info. Returns null if the referenced resource no longer exists.</summary>
    public async Task<Resolved?> ResolveAsync(ResourceType type, int resourceId, decimal factor)
    {
        switch (type)
        {
            case ResourceType.Labor:
                var l = await db.LaborResources.FirstOrDefaultAsync(r => r.Id == resourceId);
                return l is null ? null : new(R(factor * l.RatePerHour), l.RatePerHour, l.Code, l.Name);
            case ResourceType.Equipment:
                var e = await db.EquipmentResources.FirstOrDefaultAsync(r => r.Id == resourceId);
                return e is null ? null : new(R(factor * e.RatePerHour), e.RatePerHour, e.Code, e.Name);
            case ResourceType.Material:
                var m = await db.MaterialResources.FirstOrDefaultAsync(r => r.Id == resourceId);
                return m is null ? null : new(R(factor * m.UnitPrice * (1 + m.WastagePct / 100m)), m.UnitPrice, m.Code, m.Name);
            case ResourceType.Subcontractor:
                var s = await db.Subcontractors.FirstOrDefaultAsync(r => r.Id == resourceId);
                return s is null ? null : new(R(factor * s.UnitRate), s.UnitRate, s.Code, s.Name);
            default:
                return null;
        }
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
}
