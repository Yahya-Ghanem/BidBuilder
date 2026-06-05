using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

public record BenchmarkPoint(
    string ProjectCode, string ProjectName, string AreaName, string Kind, string Unit,
    decimal Quantity, decimal Total, decimal CostPerUnit, string Currency,
    decimal? CostPerUnitBase);

public record BenchmarkUnitGroup(
    string Unit, int Count, int ConvertibleCount, string BaseCurrency,
    decimal? Min, decimal? Avg, decimal? Max,
    List<string> Currencies, List<BenchmarkPoint> Points);

public record ProjectBenchmark(
    int ProjectId, string Code, string Name, string Currency,
    string? EstimateTitle, int? Revision, string? Status, decimal BidPrice);

public record BenchmarkResult(string BaseCurrency, List<ProjectBenchmark> Projects, List<BenchmarkUnitGroup> Units);

/// <summary>
/// Cross-project cost benchmarking. For each project the caller can access, takes a
/// representative estimate (latest Published, else latest revision), computes its area
/// roll-up, and collects every area that has a measure (quantity + unit) as a
/// cost-per-unit data point. Points are grouped by measure unit with min/avg/max so an
/// estimator can compare cost/m² (or cost/unit) across projects.
///
/// Cross-currency normalization: each point's cost/unit is converted to the tenant
/// <b>base currency</b> via the manual <see cref="Models.CurrencyRate"/> table
/// (RateToBase = base units per 1 unit of the currency), so the min/avg/max aggregate is
/// comparable even when projects bid in different currencies. A point whose currency has
/// no rate (and isn't the base) can't be converted — it's still listed, but excluded from
/// the aggregate (ConvertibleCount &lt; Count signals this to the UI).
///
/// Read-only/analytical — it never changes any bid. Scoped to accessible projects, so the
/// data a user sees is already limited by the project-access layer.
/// </summary>
public class BenchmarkService(AppDbContext db, ProjectAccessService access, AreaRollupService rollup)
{
    public async Task<BenchmarkResult> ComputeAsync(ClaimsPrincipal me)
    {
        var projects = await access.Accessible(me)
            .Select(p => new { p.Id, p.Code, p.Name, p.Currency })
            .ToListAsync();

        // Tenant base currency + the manual FX table → convert every cost/unit to base
        // so cross-currency aggregates are comparable.
        var baseC = ((await db.TenantSettings.Select(s => s.BaseCurrency).FirstOrDefaultAsync()) ?? "AED")
            .ToUpperInvariant();
        var rates = await db.CurrencyRates.ToDictionaryAsync(r => r.Code.ToUpperInvariant(), r => r.RateToBase);

        decimal? ToBase(decimal cpu, string currency)
        {
            var u = (currency ?? "").ToUpperInvariant();
            if (u == baseC) return cpu;                                  // already in base
            return rates.TryGetValue(u, out var rate) && rate > 0
                ? EstimateMath.Round2(cpu * rate)                        // base units per 1 unit × cpu
                : (decimal?)null;                                        // no rate → not convertible
        }

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
                        a.Quantity, a.RollupTotal, cpu, p.Currency, ToBase(cpu, p.Currency)));
        }

        var groups = points
            .GroupBy(pt => pt.Unit, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // Aggregate over base-normalized values only (skip the un-convertible).
                var conv = g.Where(x => x.CostPerUnitBase is not null)
                            .Select(x => x.CostPerUnitBase!.Value).ToList();
                decimal? min = conv.Count > 0 ? conv.Min() : null;
                decimal? max = conv.Count > 0 ? conv.Max() : null;
                decimal? avg = conv.Count > 0 ? EstimateMath.Round2(conv.Average()) : null;
                return new BenchmarkUnitGroup(
                    g.Key, g.Count(), conv.Count, baseC, min, avg, max,
                    g.Select(x => x.Currency).Distinct().OrderBy(x => x).ToList(),
                    // Sort by comparable (base) value; un-convertible points sink to the end.
                    g.OrderBy(x => x.CostPerUnitBase ?? decimal.MaxValue).ThenBy(x => x.CostPerUnit).ToList());
            })
            .OrderByDescending(g => g.Count).ThenBy(g => g.Unit)
            .ToList();

        return new BenchmarkResult(baseC, summaries.OrderBy(s => s.Code).ToList(), groups);
    }
}
