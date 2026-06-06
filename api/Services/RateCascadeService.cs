using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// Keeps cached money fields consistent when a library resource changes. Assembly
/// unit rates and estimate roll-ups are cached for speed; without this cascade a
/// rate edit would leave dependent assemblies — and the estimates built on them —
/// showing stale prices until something else happened to recompute them.
///
/// Fan-out: resource → every assembly whose component references it (recompute its
/// ComputedRate) → every estimate whose BOQ uses one of those assemblies (recompute
/// its bid). Each step is bounded (only the truly affected rows) and a no-op when
/// nothing depends on the resource.
/// </summary>
public class RateCascadeService(AppDbContext db, RateEngine engine, EstimateCalculator calc)
{
    public async Task<(int Assemblies, int Estimates)> OnResourceChangedAsync(ResourceType type, int resourceId)
    {
        // 1) Assemblies that reference this resource.
        var assemblyIds = await db.AssemblyComponents
            .Where(c => c.ResourceType == type && c.ResourceId == resourceId)
            .Select(c => c.AssemblyId)
            .Distinct()
            .ToListAsync();

        if (assemblyIds.Count == 0) return (0, 0);

        // 2) Estimates whose BOQ items are priced from any of those assemblies.
        var estimateIds = await db.BoqItems
            .Where(i => i.AssemblyId != null && assemblyIds.Contains(i.AssemblyId.Value))
            .Select(i => i.Section.EstimateId)
            .Distinct()
            .ToListAsync();

        // Atomic fan-out: recompute every affected assembly rate and every dependent
        // estimate roll-up inside ONE transaction. Without it, a failure partway leaves
        // some dependents freshly priced and others stale — exactly the inconsistency
        // this cascade exists to prevent. All-or-nothing instead. The transaction is run
        // through the execution strategy so it's retried as a unit on a transient fault.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            foreach (var aid in assemblyIds)
                await engine.RecomputeAssemblyAsync(aid);

            foreach (var eid in estimateIds)
                await calc.RecomputeAsync(eid);

            await tx.CommitAsync();
        });
        return (assemblyIds.Count, estimateIds.Count);
    }
}
