using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

public record BenchmarkPoint(
    string ProjectCode, string ProjectName, string AreaName, string Kind, string Unit,
    decimal Quantity, decimal Total, decimal CostPerUnit, string Currency);

public record BenchmarkUnitGroup(
    string Unit, int Count, decimal Min, decimal Avg, decimal Max,
    List<string> Currencies, List<BenchmarkPoint> Points);

public record ProjectBenchmark(
    int ProjectId, string Code, string Name, string Currency,
    string? EstimateTitle, int? Revision, string? Status, decimal BidPrice);

public record BenchmarkResult(List<ProjectBenchmark> Projects, List<BenchmarkUnitGroup> Units);

/// <summary>
/// Cross-project cost benchmarking. For each project the caller can access, takes a
/// representative estimate (latest Published, else latest revision), computes its area
/// roll-up, and collects every area that has a measure (quantity + unit) as a
/// cost-per-unit data point. Points are grouped by measure unit with min/avg/max so an
/// estimator can compare cost/m² (or cost/unit) across projects. Read-only/analytical —
/// it never changes any bid. Scoped to accessible projects, so the data a user sees is
/// already limited by the project-access layer.
/// </summary>
public class BenchmarkService(AppDbContext db, ProjectAccessService access, AreaRollupService rollup)
{
    public async Task<BenchmarkResult> ComputeAsync(ClaimsPrincipal me)
    {
        var projects = await access.Accessible(me)
            .Select(p => new { p.Id, p.Code, p.Name, p.Currency })
            .ToListAsync();

        var summaries = new List<ProjectBenchmark>();
        var points = new List<BenchmarkPoint>();

        foreach (var p in projects)
        {
            // Representative estimate: prefer the latest Published revision, else the
            // highest revision number.
            var est = await db.Estimates.Where(e => e.ProjectId == p.Id)
                .OrderByDescending(e => e.Status == EstimateStatus.Published)
                .ThenByDescending(e => e.Revision)
                .Select(e => new { e.Id, e.Title, e.Revision, e.Status, e.BidPrice })
                .FirstOrDefaultAsync();

            summaries.Add(new ProjectBenchmark(
                p.Id, p.Code, p.Name, p.Currency,
                est?.Title, est?.Revision, est?.Status.ToString(), est?.BidPrice ?? 0m));

            if (est is null) continue;
            var roll = await rollup.ComputeAsync(est.Id);
            if (roll is null) continue;

            foreach (var a in roll.Areas)
                if (a.Quantity > 0 && a.CostPerUnit is decimal cpu && !string.IsNullOrWhiteSpace(a.Unit))
                    points.Add(new BenchmarkPoint(
                        p.Code, p.Name, a.Name, a.Kind, a.Unit!.Trim(),
                        a.Quantity, a.RollupTotal, cpu, p.Currency));
        }

        var groups = points
            .GroupBy(pt => pt.Unit, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BenchmarkUnitGroup(
                g.Key,
                g.Count(),
                g.Min(x => x.CostPerUnit),
                EstimateMath.Round2(g.Average(x => x.CostPerUnit)),
                g.Max(x => x.CostPerUnit),
                g.Select(x => x.Currency).Distinct().OrderBy(x => x).ToList(),
                g.OrderBy(x => x.CostPerUnit).ToList()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Unit)
            .ToList();

        return new BenchmarkResult(summaries.OrderBy(s => s.Code).ToList(), groups);
    }
}
