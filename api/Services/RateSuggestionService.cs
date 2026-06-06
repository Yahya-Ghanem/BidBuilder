using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Services;

/// <summary>
/// 20.10 — AI-assisted rate suggestions.
///
/// Given a resource name, unit, and type, the service first looks for matching
/// resources already in the tenant's library and derives a suggestion from their
/// median rate (the "historical" basis). When no tenant data exists it falls back
/// to a curated industry benchmark table keyed by keyword patterns.
///
/// The three confidence tiers:
///   high   — ≥ 3 historical data points found
///   medium — 1–2 historical data points found
///   low    — benchmark-only (no tenant history for this resource)
/// </summary>
public sealed class RateSuggestionService
{
    private readonly AppDbContext  _db;
    private readonly ITenantContext _tenant;

    public RateSuggestionService(AppDbContext db, ITenantContext tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<RateSuggestion> SuggestAsync(
        string       name,
        string       unit,
        ResourceType resourceType,
        CancellationToken ct = default)
    {
        // 1. Try tenant history first.
        var comparables = await QueryHistoricalAsync(name, unit, resourceType, ct);
        if (comparables.Count > 0)
        {
            var rates  = comparables.Select(c => c.Rate).OrderBy(r => r).ToList();
            var median = Median(rates);
            return new RateSuggestion(
                SuggestedRate: Math.Round(median, 2),
                Confidence:    comparables.Count >= 3 ? "high" : "medium",
                Basis:         "historical",
                Comparables:   comparables);
        }

        // 2. Fall back to benchmark table.
        var benchmark = LookupBenchmark(name, unit, resourceType);
        return benchmark is not null
            ? benchmark
            : new RateSuggestion(0m, "low", "benchmark", []);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Query up to 10 resources of the same type whose name shares at least one
    /// word with the requested name (case-insensitive). Returns a flat list of
    /// comparable rates (one per resource, from the most recent rate snapshot or
    /// the live resource rate if no history exists).
    /// </summary>
    private async Task<IReadOnlyList<ComparableRate>> QueryHistoricalAsync(
        string name, string unit, ResourceType resourceType, CancellationToken ct)
    {
        // Break the query into significant words (≥3 chars), skip stop-words.
        var words = Tokenise(name);
        if (words.Length == 0) return [];

        switch (resourceType)
        {
            case ResourceType.Labor:
            {
                var rows = await _db.LaborResources
                    .Where(r => r.IsActive)
                    .ToListAsync(ct);
                return rows
                    .Where(r => MatchesAnyWord(r.Name, words))
                    .Take(10)
                    .Select(r => new ComparableRate(r.Name, r.Unit, r.RatePerHour, "library"))
                    .ToList();
            }
            case ResourceType.Material:
            {
                var rows = await _db.MaterialResources
                    .Where(r => r.IsActive)
                    .ToListAsync(ct);
                return rows
                    .Where(r => MatchesAnyWord(r.Name, words))
                    .Take(10)
                    .Select(r => new ComparableRate(r.Name, r.Unit, r.UnitPrice, "library"))
                    .ToList();
            }
            case ResourceType.Equipment:
            {
                var rows = await _db.EquipmentResources
                    .Where(r => r.IsActive)
                    .ToListAsync(ct);
                return rows
                    .Where(r => MatchesAnyWord(r.Name, words))
                    .Take(10)
                    .Select(r => new ComparableRate(r.Name, r.Unit, r.RatePerHour, "library"))
                    .ToList();
            }
            case ResourceType.Subcontractor:
            {
                var rows = await _db.Subcontractors
                    .Where(r => r.IsActive)
                    .ToListAsync(ct);
                return rows
                    .Where(r => MatchesAnyWord(r.Name, words))
                    .Take(10)
                    .Select(r => new ComparableRate(r.Name, r.Unit, r.UnitRate, "library"))
                    .ToList();
            }
            default:
                return [];
        }
    }

    // ── Benchmark table ───────────────────────────────────────────────────────

    /// <summary>
    /// Industry mid-point benchmarks by keyword. All rates in USD-equivalent;
    /// estimators should use these only as a starting point and validate against
    /// their own supplier/payroll data.
    /// </summary>
    private static RateSuggestion? LookupBenchmark(string name, string unit, ResourceType type)
    {
        var n = name.ToLowerInvariant();

        var matches = Benchmarks
            .Where(b => b.Type == type && b.Keywords.Any(kw => n.Contains(kw)))
            .ToList();

        if (matches.Count == 0) return null;

        // Prefer the most-specific entry (most keywords matched).
        var best = matches.OrderByDescending(b => b.Keywords.Count(kw => n.Contains(kw))).First();

        return new RateSuggestion(
            SuggestedRate: best.Rate,
            Confidence:    "low",
            Basis:         "benchmark",
            Comparables: [new ComparableRate(best.Label, best.Unit, best.Rate, "industry benchmark")]);
    }

    // ── Median helper ─────────────────────────────────────────────────────────

    private static decimal Median(IList<decimal> sorted)
    {
        int n = sorted.Count;
        if (n == 0) return 0m;
        if (n % 2 == 1) return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2m;
    }

    // ── Tokeniser ─────────────────────────────────────────────────────────────

    private static readonly HashSet<string> StopWords =
        ["the", "and", "for", "per", "with", "all", "any", "one", "two"];

    private static string[] Tokenise(string s) =>
        s.ToLowerInvariant()
         .Split([' ', '-', '_', '/', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
         .Where(w => w.Length >= 3 && !StopWords.Contains(w))
         .Distinct()
         .ToArray();

    private static bool MatchesAnyWord(string candidate, string[] words)
    {
        var lower = candidate.ToLowerInvariant();
        return words.Any(w => lower.Contains(w));
    }

    // ── Benchmark data ────────────────────────────────────────────────────────

    private sealed record BenchmarkEntry(
        ResourceType Type, string[] Keywords, string Label, string Unit, decimal Rate);

    private static readonly BenchmarkEntry[] Benchmarks =
    [
        // ── Labor ─────────────────────────────────────────────────────────────
        new(ResourceType.Labor, ["mason", "bricklayer"],        "Mason",                  "hr",  22.00m),
        new(ResourceType.Labor, ["carpenter", "joiner"],        "Carpenter",              "hr",  24.00m),
        new(ResourceType.Labor, ["electrician", "electrical"],  "Electrician",            "hr",  32.00m),
        new(ResourceType.Labor, ["plumber", "plumbing"],        "Plumber",                "hr",  30.00m),
        new(ResourceType.Labor, ["welder", "welding"],          "Welder",                 "hr",  28.00m),
        new(ResourceType.Labor, ["steel", "rebar", "fixer"],    "Steel Fixer",            "hr",  20.00m),
        new(ResourceType.Labor, ["painter", "painting"],        "Painter",                "hr",  18.00m),
        new(ResourceType.Labor, ["finisher", "plastering"],     "Plasterer / Finisher",   "hr",  19.00m),
        new(ResourceType.Labor, ["tiler", "tiling"],            "Tiler",                  "hr",  21.00m),
        new(ResourceType.Labor, ["labourer", "laborer", "unskilled"], "General Labourer", "hr",  13.50m),
        new(ResourceType.Labor, ["foreman", "supervisor"],      "Site Foreman",           "hr",  35.00m),
        new(ResourceType.Labor, ["engineer", "technician"],     "Site Engineer",          "hr",  40.00m),
        new(ResourceType.Labor, ["driver", "operator"],         "Plant Operator",         "hr",  22.00m),
        new(ResourceType.Labor, ["surveyor"],                   "Quantity Surveyor",      "hr",  55.00m),
        new(ResourceType.Labor, ["manager", "project manager"], "Project Manager",        "hr",  70.00m),

        // ── Material ──────────────────────────────────────────────────────────
        new(ResourceType.Material, ["concrete", "ready-mix"],   "Ready-Mix Concrete C30", "m3",   95.00m),
        new(ResourceType.Material, ["concrete", "c25"],         "Concrete Grade C25",     "m3",   85.00m),
        new(ResourceType.Material, ["concrete", "c40"],         "Concrete Grade C40",     "m3",  110.00m),
        new(ResourceType.Material, ["steel", "rebar", "reinforcement"], "Rebar 16mm",     "tonne",780.00m),
        new(ResourceType.Material, ["steel", "structural"],     "Structural Steel",       "tonne",900.00m),
        new(ResourceType.Material, ["brick", "clay"],           "Clay Brick",             "each",   0.55m),
        new(ResourceType.Material, ["block", "masonry"],        "Concrete Block 200mm",   "each",   1.20m),
        new(ResourceType.Material, ["sand"],                    "Building Sand",          "m3",   28.00m),
        new(ResourceType.Material, ["gravel", "aggregate"],     "Coarse Aggregate",       "m3",   35.00m),
        new(ResourceType.Material, ["cement"],                  "Ordinary Portland Cement","bag",   8.00m),
        new(ResourceType.Material, ["timber", "lumber"],        "Structural Timber",      "m3",  350.00m),
        new(ResourceType.Material, ["plywood"],                 "Plywood 18mm",           "m2",   14.00m),
        new(ResourceType.Material, ["glass", "glazing"],        "Float Glass 6mm",        "m2",   22.00m),
        new(ResourceType.Material, ["tile", "ceramic"],         "Ceramic Floor Tile",     "m2",   18.00m),
        new(ResourceType.Material, ["insulation"],              "Mineral Wool Insulation", "m2",    7.50m),
        new(ResourceType.Material, ["pipe", "pvc"],             "PVC Pipe 110mm",         "m",     4.50m),
        new(ResourceType.Material, ["pipe", "steel"],           "Steel Pipe 50mm",        "m",    15.00m),
        new(ResourceType.Material, ["waterproofing"],           "Waterproof Membrane",    "m2",   12.00m),
        new(ResourceType.Material, ["paint"],                   "Exterior Emulsion Paint","litre",  4.00m),
        new(ResourceType.Material, ["formwork"],                "Formwork Ply",           "m2",   10.00m),
        new(ResourceType.Material, ["diesel", "fuel"],          "Diesel Fuel",            "litre",  0.90m),

        // ── Equipment ─────────────────────────────────────────────────────────
        new(ResourceType.Equipment, ["excavator", "digger"],          "Excavator 20t",    "hr",  80.00m),
        new(ResourceType.Equipment, ["excavator", "mini"],            "Mini Excavator 3t","hr",  45.00m),
        new(ResourceType.Equipment, ["crane", "tower"],               "Tower Crane",      "hr", 140.00m),
        new(ResourceType.Equipment, ["crane", "mobile"],              "Mobile Crane 50t", "hr", 110.00m),
        new(ResourceType.Equipment, ["dozer", "bulldozer"],           "Bulldozer D6",     "hr",  90.00m),
        new(ResourceType.Equipment, ["loader", "wheel"],              "Wheel Loader",     "hr",  65.00m),
        new(ResourceType.Equipment, ["dump", "truck", "tipper"],      "Dump Truck 10t",   "hr",  55.00m),
        new(ResourceType.Equipment, ["concrete", "mixer", "drum"],    "Concrete Mixer",   "hr",  18.00m),
        new(ResourceType.Equipment, ["pump", "concrete"],             "Concrete Pump",    "hr",  75.00m),
        new(ResourceType.Equipment, ["vibrator", "poker"],            "Concrete Vibrator","hr",   5.00m),
        new(ResourceType.Equipment, ["scaffold", "scaffolding"],      "Scaffolding",      "m2",   6.00m),
        new(ResourceType.Equipment, ["compactor", "plate", "wacker"], "Plate Compactor",  "hr",  12.00m),
        new(ResourceType.Equipment, ["generator"],                    "Generator 50kVA",  "hr",  20.00m),
        new(ResourceType.Equipment, ["forklift"],                     "Forklift 3t",      "hr",  35.00m),
        new(ResourceType.Equipment, ["piling", "rig"],                "Piling Rig",       "hr", 180.00m),
        new(ResourceType.Equipment, ["grader", "motor"],              "Motor Grader",     "hr",  95.00m),

        // ── Subcontractor ─────────────────────────────────────────────────────
        new(ResourceType.Subcontractor, ["painting", "decoration"],   "Painting & Decoration","m2", 14.00m),
        new(ResourceType.Subcontractor, ["electrical", "wiring"],     "Electrical Wiring","point", 60.00m),
        new(ResourceType.Subcontractor, ["plumbing"],                 "Plumbing Works",   "point", 80.00m),
        new(ResourceType.Subcontractor, ["hvac", "mechanical"],       "HVAC Installation","m2",    45.00m),
        new(ResourceType.Subcontractor, ["landscaping"],              "Landscaping",      "m2",    18.00m),
        new(ResourceType.Subcontractor, ["ndt", "testing"],           "NDT Testing",      "weld",  25.00m),
        new(ResourceType.Subcontractor, ["cleaning", "housekeeping"], "Site Cleaning",    "m2",     3.50m),
        new(ResourceType.Subcontractor, ["security", "guard"],        "Security",         "day",   120.00m),
        new(ResourceType.Subcontractor, ["survey", "setting-out"],    "Setting Out Survey","day",  350.00m),
        new(ResourceType.Subcontractor, ["cctv", "surveillance"],     "CCTV Installation","point", 200.00m),
        new(ResourceType.Subcontractor, ["fire", "suppression"],      "Fire Suppression", "point", 250.00m),
        new(ResourceType.Subcontractor, ["glazing", "curtain"],       "Curtain Wall",     "m2",   180.00m),
    ];
}

// ── Response records (shared with endpoint + tests) ───────────────────────────

public record RateSuggestion(
    decimal SuggestedRate,
    string  Confidence,     // "high" | "medium" | "low"
    string  Basis,          // "historical" | "benchmark"
    IReadOnlyList<ComparableRate> Comparables);

public record ComparableRate(
    string  Name,
    string  Unit,
    decimal Rate,
    string  Source);
