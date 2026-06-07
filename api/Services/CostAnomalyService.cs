using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// 23.5 — Cost anomaly detection (no LLM, no calls out).
///
/// For each BOQ line in an estimate, looks up that line's unit-rate against the
/// tenant's history of comparable lines (same Unit). Flags a line when either:
///
///   1. The line's UnitRate is &gt;= <c>ZScoreThreshold</c> standard deviations away
///      from the historical mean, OR
///   2. The line's UnitRate is &gt; <c>MedianMultipleThreshold</c> × the historical
///      median (a robust, scale-aware sanity check that catches "10× the going
///      rate" mistakes even when the historical distribution is narrow enough
///      that <c>stddev</c> is tiny).
///
/// "Comparable" means: same Unit (kg, m2, m3, hr …) and at least one shared
/// significant word in the description (length ≥ 3, stop-words filtered). When the
/// description-token filter leaves &lt; <c>MinHistoryForZ</c> rows we fall back to
/// the broader Unit-only set; if even that has &lt; <c>MinHistoryForZ</c> rows we
/// skip the line (not enough signal to flag honestly).
///
/// History always comes from <i>other</i> estimates in the tenant — the estimate
/// being analyzed never feeds its own benchmark, so a uniformly-overpriced sheet
/// can still be flagged.
/// </summary>
public sealed class CostAnomalyService
{
    private readonly AppDbContext _db;

    public CostAnomalyService(AppDbContext db) { _db = db; }

    // Thresholds. Exposed as consts so tests can reference them and so the
    // numbers live next to the algorithm they govern.
    public const double  ZScoreThreshold              = 2.5;
    public const decimal MedianMultipleThresholdHigh  = 3.0m;     // overpriced trigger
    public const decimal MedianMultipleThresholdLow   = 1m / 3m;  // underpriced trigger (1/3 of median)
    public const decimal MedianMultipleSevereHigh     = 5.0m;     // promotes severity to "high"
    public const decimal MedianMultipleSevereLow      = 0.2m;     // ditto for the underpriced side
    public const int     MinHistoryForZ               = 5;

    public async Task<AnomalyReport> AnalyzeAsync(int estimateId, CancellationToken ct = default)
    {
        var estimate = await _db.Estimates
            .Include(e => e.Sections).ThenInclude(s => s.Items)
            .FirstOrDefaultAsync(e => e.Id == estimateId, ct);
        if (estimate is null) return new AnomalyReport(estimateId, 0, 0, []);

        // Pull every other-estimate line in the tenant (query filter already
        // restricts to the active tenant). Comparable history for ONE line is
        // expected to be at most a few thousand rows in practice; we load them
        // once and bucket in-memory rather than running an aggregate per line.
        var history = await _db.BoqItems
            .Where(i => i.Section.EstimateId != estimateId
                     && i.UnitRate > 0m
                     && i.Kind == BoqItemKind.Normal)
            .Select(i => new HistoryRow(i.Unit, i.Description, i.UnitRate))
            .ToListAsync(ct);

        var byUnit = history
            .Where(r => !string.IsNullOrWhiteSpace(r.Unit))
            .GroupBy(r => r.Unit.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var items   = estimate.Sections.SelectMany(s => s.Items).ToList();
        var flagged = new List<AnomalyItem>();

        foreach (var item in items.Where(i => i.Kind == BoqItemKind.Normal && i.UnitRate > 0m))
        {
            var unitKey = (item.Unit ?? "").Trim().ToLowerInvariant();
            if (!byUnit.TryGetValue(unitKey, out var unitBucket) || unitBucket.Count < MinHistoryForZ)
                continue;

            // Try the narrow (token-matched) bucket first; fall back to unit-only.
            var tokens = Tokenise(item.Description);
            var narrow = tokens.Length == 0
                ? unitBucket
                : unitBucket.Where(r => Tokenise(r.Description).Any(t => tokens.Contains(t))).ToList();
            var bucket = narrow.Count >= MinHistoryForZ ? narrow : unitBucket;
            var scope  = narrow.Count >= MinHistoryForZ ? "unit+desc" : "unit";

            var rates = bucket.Select(r => r.Rate).ToList();
            var mean  = rates.Average();
            var (median, stddev) = Stats(rates);

            // z-score: how many stddevs is this line from the mean?
            var z = stddev > 0m
                ? (double)((item.UnitRate - mean) / stddev)
                : 0.0;

            // Median multiple: scale-aware sanity check, immune to a near-zero stddev.
            var medianMultiple = median > 0m ? item.UnitRate / median : 0m;

            var byZ      = stddev > 0m && Math.Abs(z) >= ZScoreThreshold;
            // Median-multiple is symmetric: fires either when the line is much HIGHER
            // (>= 3× median, a 10x-mistake guard immune to a near-zero stddev), or much
            // LOWER (<= 1/3 of median — missing zero, wrong unit, decimal slip).
            var byMedian = median > 0m
                        && (medianMultiple >= MedianMultipleThresholdHigh
                         || medianMultiple <= MedianMultipleThresholdLow);
            if (!byZ && !byMedian) continue;

            // Severity: shouting-loud cases get "high":
            //   • both triggers fired
            //   • |z| >= 3.5
            //   • extreme median multiple (>= 5× or <= 1/5)
            // Otherwise "medium".
            var extremeMedian = median > 0m
                             && (medianMultiple >= MedianMultipleSevereHigh
                              || medianMultiple <= MedianMultipleSevereLow);
            var severity = (byZ && byMedian) || Math.Abs(z) >= 3.5 || extremeMedian
                ? "high"
                : "medium";

            var reason = (byZ, byMedian) switch
            {
                (true,  true)  => $"|z|={Math.Abs(z):F1} and {medianMultiple:F1}× median",
                (true,  false) => $"|z|={Math.Abs(z):F1} vs {scope} history",
                (false, true)  => $"{medianMultiple:F1}× median ({scope} history)",
                _              => "",
            };

            flagged.Add(new AnomalyItem(
                ItemId:         item.Id,
                SectionId:      item.SectionId,
                ItemCode:       item.ItemCode,
                Description:    item.Description,
                Unit:           item.Unit ?? "",
                Quantity:       item.Quantity,
                UnitRate:       item.UnitRate,
                LineTotal:      item.LineTotal,
                HistoricalMean: Math.Round(mean,   4),
                HistoricalMedian: Math.Round(median, 4),
                ZScore:         Math.Round(z,      2),
                MedianMultiple: Math.Round(medianMultiple, 2),
                Severity:       severity,
                Reason:         reason,
                SampleSize:     bucket.Count));
        }

        return new AnomalyReport(
            EstimateId: estimateId,
            ItemsScanned: items.Count,
            ItemsFlagged: flagged.Count,
            Items: flagged.OrderByDescending(a => a.Severity == "high")
                          .ThenByDescending(a => Math.Abs(a.ZScore))
                          .ToList());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (decimal median, decimal stddev) Stats(IList<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var n      = sorted.Count;
        var median = n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2m;

        var mean = values.Average();
        var sumSq = 0m;
        foreach (var v in values) sumSq += (v - mean) * (v - mean);
        // Population stddev — we have the full historical population for this bucket.
        var variance = sumSq / n;
        var stddev   = (decimal)Math.Sqrt((double)variance);
        return (median, stddev);
    }

    private static readonly HashSet<string> StopWords =
        ["the", "and", "for", "per", "with", "all", "any", "one", "two", "from"];

    private static string[] Tokenise(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        return s.ToLowerInvariant()
                .Split([' ', '-', '_', '/', ',', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3 && !StopWords.Contains(w))
                .Distinct()
                .ToArray();
    }

    private sealed record HistoryRow(string Unit, string Description, decimal Rate);
}

// ── Response records (shared with endpoint + tests) ───────────────────────────

public record AnomalyReport(
    int EstimateId,
    int ItemsScanned,
    int ItemsFlagged,
    IReadOnlyList<AnomalyItem> Items);

public record AnomalyItem(
    int    ItemId,
    int    SectionId,
    string ItemCode,
    string Description,
    string Unit,
    decimal Quantity,
    decimal UnitRate,
    decimal LineTotal,
    decimal HistoricalMean,
    decimal HistoricalMedian,
    double  ZScore,
    decimal MedianMultiple,
    string  Severity,   // "high" | "medium"
    string  Reason,
    int     SampleSize);
