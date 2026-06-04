using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;

namespace BidBuilder.Api.Services;

public record AreaRollupRow(int Id, int? ParentAreaId, string Name, string Kind, decimal DirectTotal, decimal RollupTotal, int ItemCount);
public record AreaRollupResult(string Currency, List<AreaRollupRow> Areas, decimal AssignedTotal, decimal UnassignedTotal);

/// <summary>
/// Computes a project Area cost roll-up for one estimate: each item's line total is
/// summed into its area (direct), then escalated up the tree (unit → sub-area → area
/// → project). Shared by the areas-rollup endpoint and the exporters.
/// </summary>
public class AreaRollupService(AppDbContext db)
{
    public async Task<AreaRollupResult?> ComputeAsync(int estimateId)
    {
        var est = await db.Estimates.Where(e => e.Id == estimateId)
            .Select(e => new { e.ProjectId, e.Currency }).FirstOrDefaultAsync();
        if (est is null) return null;

        var areas = await db.Areas.Where(a => a.ProjectId == est.ProjectId)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync();
        var items = await db.BoqItems.Where(it => it.Section.EstimateId == estimateId)
            .Select(it => new { it.AreaId, it.LineTotal }).ToListAsync();

        var direct = areas.ToDictionary(a => a.Id, _ => 0m);
        var count = areas.ToDictionary(a => a.Id, _ => 0);
        decimal unassigned = 0m;
        foreach (var it in items)
        {
            if (it.AreaId is int aid && direct.ContainsKey(aid)) { direct[aid] += it.LineTotal; count[aid]++; }
            else unassigned += it.LineTotal;
        }

        var childrenOf = areas.Where(a => a.ParentAreaId is not null)
            .GroupBy(a => a.ParentAreaId!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        var rollup = new Dictionary<int, decimal>();
        decimal Roll(int areaId)
        {
            if (rollup.TryGetValue(areaId, out var cached)) return cached;
            var total = direct[areaId];
            if (childrenOf.TryGetValue(areaId, out var kids)) foreach (var k in kids) total += Roll(k);
            return rollup[areaId] = total;
        }
        foreach (var a in areas) Roll(a.Id);

        var rows = areas.Select(a => new AreaRollupRow(
            a.Id, a.ParentAreaId, a.Name, a.Kind.ToString(),
            EstimateMath.Round2(direct[a.Id]), EstimateMath.Round2(rollup[a.Id]), count[a.Id])).ToList();
        var assigned = areas.Where(a => a.ParentAreaId is null).Sum(a => rollup[a.Id]);
        return new AreaRollupResult(est.Currency, rows, EstimateMath.Round2(assigned), EstimateMath.Round2(unassigned));
    }
}
