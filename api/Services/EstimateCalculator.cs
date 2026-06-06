using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

// ── Breakdown DTOs returned to the client ─────────────────────────────────────
public record EstimateBreakdown(
    int Id, int ProjectId, int Revision, string Title, string Status, string Currency,
    decimal DirectCost, decimal IndirectCost, decimal MarkupCost, decimal BidPrice,
    List<SectionBreakdown> Sections, List<PrelimBreakdown> Preliminaries, List<MarkupBreakdown> Markups,
    string RowVersion, FxView? Fx);

/// <summary>The bid expressed in the estimate's secondary (presentation) currency.
/// <c>Rate</c> is the native→secondary factor; <c>Frozen</c> = snapshotted at publish.</summary>
public record FxView(string SecondaryCurrency, decimal Rate, decimal ConvertedBidPrice, bool Frozen, DateTime? FrozenAt);

public record SectionBreakdown(int Id, string Code, string Title, int SortOrder, decimal SectionTotal, List<ItemBreakdown> Items);
public record ItemBreakdown(int Id, string ItemCode, string Description, string Unit, decimal Quantity, int? AssemblyId, decimal UnitRate, decimal LineTotal, int SortOrder,
    List<ItemCostComponentBreakdown> Components, int? AreaId);
/// <summary>One cost-component line of an item's unit-rate build-up.
/// <c>Value</c> is the entered figure (money for Amount, % for Percent);
/// <c>Amount</c> is its money contribution to the unit rate.</summary>
public record ItemCostComponentBreakdown(int TypeId, string Code, string Name, string CalcKind, decimal Value, decimal Amount, decimal? Quantity, decimal? Rate);
public record PrelimBreakdown(int Id, string Description, string Kind, decimal Amount, decimal ComputedTotal, int SortOrder);
public record MarkupBreakdown(int Id, string Type, string? Label, decimal Percentage, int ApplyOrder, decimal ComputedAmount);

// ── What-if (non-persisting margin preview) ───────────────────────────────────
public record WhatIfMarkupInput(string Type, string? Label, decimal Percentage, int ApplyOrder);
public record WhatIfMarkupLine(string Type, string? Label, decimal Percentage, int ApplyOrder, decimal ComputedAmount);
public record WhatIfResult(
    decimal DirectCost, decimal IndirectCost, decimal MarkupCost,
    decimal BidPrice, decimal BaselineBidPrice, List<WhatIfMarkupLine> Markups);

// ── Reconcile (drift detection between cached and freshly-computed totals) ─────
/// <summary>The four cached roll-up totals of an estimate.</summary>
public record EstimateTotals(decimal DirectCost, decimal IndirectCost, decimal MarkupCost, decimal BidPrice);
/// <summary>Result of a reconcile: the cached totals (<c>Before</c>), the freshly
/// recomputed totals (<c>After</c>), and whether they differ. When they differ the
/// stored cache has drifted from what the engine now produces.</summary>
public record ReconcileResult(EstimateTotals Before, EstimateTotals After, bool Drifted);

/// <summary>
/// Loads an estimate's full graph, recomputes every cached money field via the
/// pure <see cref="EstimateMath"/>, persists, and returns a breakdown. A BOQ item
/// priced from an assembly takes that assembly's ComputedRate; otherwise its
/// ad-hoc UnitRate stands.
/// </summary>
public class EstimateCalculator(AppDbContext db)
{
    public async Task<EstimateBreakdown?> RecomputeAsync(int estimateId)
    {
        var e = await LoadGraphAsync(estimateId);
        if (e is null) return null;
        var (ordered, typeMap) = await ComputeAsync(e);
        await db.SaveChangesAsync();
        return Build(e, ordered, RowVersionOf(e), await BuildFxAsync(e), typeMap);
    }

    /// <summary>
    /// Reconcile the stored cached totals against what the engine produces now.
    /// Reports the cached values (<c>Before</c>), the freshly computed values
    /// (<c>After</c>), and whether they differ (drift). Persists the corrected
    /// values ONLY when <paramref name="commit"/> is true; otherwise it is a pure
    /// read — the recomputed entity is discarded with the request scope. This is the
    /// safety net for the denormalised money cache: it proves (or repairs) that
    /// DirectCost/IndirectCost/MarkupCost/BidPrice still equal a clean recomputation.
    /// </summary>
    public async Task<ReconcileResult?> ReconcileAsync(int estimateId, bool commit)
    {
        var e = await LoadGraphAsync(estimateId);
        if (e is null) return null;

        var before = new EstimateTotals(e.DirectCost, e.IndirectCost, e.MarkupCost, e.BidPrice);
        await ComputeAsync(e);
        var after = new EstimateTotals(e.DirectCost, e.IndirectCost, e.MarkupCost, e.BidPrice);
        var drifted = before != after;

        if (commit && drifted) await db.SaveChangesAsync();
        return new ReconcileResult(before, after, drifted);
    }

    /// <summary>Load an estimate's full pricing graph (project, BOQ tree + component
    /// lines, preliminaries, markups) for a recompute/reconcile.</summary>
    private Task<Estimate?> LoadGraphAsync(int estimateId) => db.Estimates
        .Include(x => x.Project)
        .Include(x => x.Sections).ThenInclude(s => s.Items).ThenInclude(i => i.CostComponents)
        .Include(x => x.Preliminaries)
        .Include(x => x.Markups)
        .FirstOrDefaultAsync(x => x.Id == estimateId);

    /// <summary>
    /// The single calculation authority: recompute every cached money field on the
    /// loaded estimate via the pure <see cref="EstimateMath"/> — item unit rates &amp;
    /// line totals, section totals, direct cost, preliminaries &amp; indirect cost, the
    /// compounding markups, and the bid price. Mutates the tracked entity in place but
    /// does NOT save; callers decide whether to persist. Returns the ordered markups
    /// and the cost-component type map for building the breakdown DTO.
    /// </summary>
    private async Task<(List<Markup> Ordered, Dictionary<int, CostComponentType> TypeMap)> ComputeAsync(Estimate e)
    {
        // Resolve assembly rates for any assembly-priced items in one query.
        var assemblyIds = e.Sections.SelectMany(s => s.Items)
                                    .Where(i => i.AssemblyId is not null)
                                    .Select(i => i.AssemblyId!.Value).Distinct().ToList();
        var rates = await db.Assemblies.Where(a => assemblyIds.Contains(a.Id))
                                       .ToDictionaryAsync(a => a.Id, a => a.ComputedRate);
        var typeMap = await db.CostComponentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t);

        // ── Direct cost: items → sections ────────────────────────────────────
        // Rate precedence: cost-component build-up → assembly rate → ad-hoc rate.
        decimal direct = 0m;
        foreach (var s in e.Sections)
        {
            decimal sectionTotal = 0m;
            foreach (var i in s.Items)
            {
                if (i.CostComponents.Count > 0)
                    i.UnitRate = BuildItemRate(i.CostComponents, typeMap).Rate;
                else if (i.AssemblyId is not null && rates.TryGetValue(i.AssemblyId.Value, out var rate))
                    i.UnitRate = rate;                       // assembly-priced: rate is derived
                i.LineTotal = EstimateMath.LineTotal(i.Quantity, i.UnitRate);
                sectionTotal += i.LineTotal;
            }
            s.SectionTotal = EstimateMath.Round2(sectionTotal);
            direct += s.SectionTotal;
        }

        // ── Indirect cost: preliminaries ─────────────────────────────────────
        var durationMonths = e.Project.DurationMonths ?? 1;
        decimal indirect = 0m;
        foreach (var p in e.Preliminaries)
        {
            p.ComputedTotal = EstimateMath.PreliminaryTotal(p.Kind, p.Amount, durationMonths);
            indirect += p.ComputedTotal;
        }

        // ── Markups: compounding in ApplyOrder ───────────────────────────────
        var ordered = e.Markups.OrderBy(m => m.ApplyOrder).ToList();
        var (amounts, markupTotal, _) = EstimateMath.ApplyMarkups(
            direct + indirect, ordered.Select(m => m.Percentage).ToList());
        for (int k = 0; k < ordered.Count; k++) ordered[k].ComputedAmount = amounts[k];

        e.DirectCost   = EstimateMath.Round2(direct);
        e.IndirectCost = EstimateMath.Round2(indirect);
        e.MarkupCost   = EstimateMath.Round2(markupTotal);
        e.BidPrice     = EstimateMath.Round2(direct + indirect + markupTotal);
        e.UpdatedAt    = DateTime.UtcNow;

        return (ordered, typeMap);
    }

    /// <summary>
    /// Build a BOQ item's unit rate from its cost-component lines and the type catalog.
    /// Amount-kind lines sum to a subtotal; each Percent-kind line adds (pct% × subtotal).
    /// Returns the rate plus the per-line money breakdown (ordered by type SortOrder).
    /// </summary>
    public static (decimal Rate, List<ItemCostComponentBreakdown> Lines) BuildItemRate(
        ICollection<ItemCostComponent> comps, IReadOnlyDictionary<int, CostComponentType> typeMap)
    {
        decimal amountSubtotal = 0m, percentSum = 0m;
        foreach (var c in comps)
            if (typeMap.TryGetValue(c.CostComponentTypeId, out var t) && t.CalcKind == CostCalcKind.Amount)
                amountSubtotal += c.Value;

        var lines = new List<ItemCostComponentBreakdown>();
        foreach (var c in comps)
        {
            if (!typeMap.TryGetValue(c.CostComponentTypeId, out var t)) continue;
            var money = t.CalcKind == CostCalcKind.Amount
                ? c.Value
                : EstimateMath.Round2(amountSubtotal * c.Value / 100m);
            if (t.CalcKind == CostCalcKind.Percent) percentSum += c.Value;
            lines.Add(new ItemCostComponentBreakdown(t.Id, t.Code, t.Name, t.CalcKind.ToString(), c.Value, money, c.Quantity, c.Rate));
        }
        var rate = amountSubtotal + amountSubtotal * percentSum / 100m;
        return (rate, lines.OrderBy(l => typeMap[l.TypeId].SortOrder).ToList());
    }

    /// <summary>
    /// Manual cross-rate: how many units of <paramref name="target"/> equal one unit
    /// of <paramref name="source"/>, derived via the tenant base currency. Each stored
    /// RateToBase is base-currency units per 1 unit of that currency (base itself = 1),
    /// so factor(S→T) = rateToBase[S] / rateToBase[T].
    /// Returns null when either currency lacks a rate (and isn't the base).
    /// </summary>
    public async Task<decimal?> CrossRateAsync(string? source, string? target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return null;
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return 1m;

        var baseC = await db.TenantSettings.Select(s => s.BaseCurrency).FirstOrDefaultAsync() ?? "AED";
        var needed = new[] { source, target }
            .Where(c => !string.Equals(c, baseC, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ToUpperInvariant()).Distinct().ToList();
        var map = await db.CurrencyRates.Where(r => needed.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.RateToBase);

        // Value of 1 unit of c in base-currency units (base = 1).
        decimal? R(string c) =>
            string.Equals(c, baseC, StringComparison.OrdinalIgnoreCase) ? 1m
            : map.TryGetValue(c.ToUpperInvariant(), out var v) ? v : (decimal?)null;

        var s = R(source); var t = R(target);
        return (s is null || t is null || t.Value == 0m) ? null : s.Value / t.Value;
    }

    /// <summary>Build the presentation-currency view (null if none set / not convertible).
    /// Uses the frozen FxRate when present (Published), otherwise the live tenant rate.</summary>
    private async Task<FxView?> BuildFxAsync(Estimate e)
    {
        if (string.IsNullOrWhiteSpace(e.SecondaryCurrency)) return null;
        var sec = e.SecondaryCurrency!.ToUpperInvariant();
        if (string.Equals(sec, e.Currency, StringComparison.OrdinalIgnoreCase)) return null;

        decimal rate; bool frozen; DateTime? at;
        if (e.FxRate is { } fr) { rate = fr; frozen = true; at = e.FxRateAt; }
        else
        {
            var live = await CrossRateAsync(e.Currency, sec);
            if (live is null) return null;
            rate = live.Value; frozen = false; at = null;
        }
        return new FxView(sec, rate, EstimateMath.Round2(e.BidPrice * rate), frozen, at);
    }

    /// <summary>
    /// Optimistic-concurrency guard. When the caller supplies the row version it
    /// last saw (the estimate's Postgres xmin), pin it as the original value and
    /// touch the row so the upcoming SaveChanges issues an UPDATE … WHERE xmin =
    /// expected. A concurrent edit will have advanced xmin → 0 rows → EF throws
    /// DbUpdateConcurrencyException, which the endpoint filter maps to 409.
    /// No-op when no version is supplied (last-writer-wins, backward compatible).
    /// </summary>
    public async Task GuardVersionAsync(int estimateId, string? expectedRowVersion)
    {
        if (string.IsNullOrWhiteSpace(expectedRowVersion)) return;
        if (!uint.TryParse(expectedRowVersion, out var expected)) return;
        var e = await db.Estimates.FirstOrDefaultAsync(x => x.Id == estimateId);
        if (e is null) return;
        db.Entry(e).Property("xmin").OriginalValue = expected;
        e.UpdatedAt = DateTime.UtcNow;   // ensure the estimate row participates in the UPDATE
    }

    private string RowVersionOf(Estimate e) =>
        db.Entry(e).Property<uint>("xmin").CurrentValue.ToString();

    /// <summary>
    /// Margin what-if: re-prices the bid using a proposed set of markup percentages
    /// against the estimate's current direct + indirect cost, WITHOUT persisting
    /// anything. Lets an estimator try "what's our number at 8% profit?" instantly.
    /// Uses the cached DirectCost/IndirectCost (kept current by every mutation).
    /// </summary>
    public async Task<WhatIfResult?> WhatIfAsync(int estimateId, IReadOnlyList<WhatIfMarkupInput> markups)
    {
        var e = await db.Estimates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == estimateId);
        if (e is null) return null;

        var ordered = markups.OrderBy(m => m.ApplyOrder).ToList();
        var baseAmount = e.DirectCost + e.IndirectCost;
        var (amounts, markupTotal, _) = EstimateMath.ApplyMarkups(
            baseAmount, ordered.Select(m => m.Percentage).ToList());

        var lines = ordered.Select((m, k) =>
            new WhatIfMarkupLine(m.Type, m.Label, m.Percentage, m.ApplyOrder, amounts[k])).ToList();

        return new WhatIfResult(
            EstimateMath.Round2(e.DirectCost),
            EstimateMath.Round2(e.IndirectCost),
            EstimateMath.Round2(markupTotal),
            EstimateMath.Round2(baseAmount + markupTotal),
            e.BidPrice,
            lines);
    }

    /// <summary>Read-only breakdown without recomputing (uses cached values).</summary>
    public async Task<EstimateBreakdown?> GetAsync(int estimateId)
    {
        var e = await db.Estimates
            .Include(x => x.Sections).ThenInclude(s => s.Items).ThenInclude(i => i.CostComponents)
            .Include(x => x.Preliminaries)
            .Include(x => x.Markups)
            .FirstOrDefaultAsync(x => x.Id == estimateId);
        if (e is null) return null;
        var typeMap = await db.CostComponentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t);
        return Build(e, e.Markups.OrderBy(m => m.ApplyOrder).ToList(), RowVersionOf(e), await BuildFxAsync(e), typeMap);
    }

    private static EstimateBreakdown Build(Estimate e, List<Markup> orderedMarkups, string rowVersion, FxView? fx,
        IReadOnlyDictionary<int, CostComponentType> typeMap) => new(
        e.Id, e.ProjectId, e.Revision, e.Title, e.Status.ToString(), e.Currency,
        e.DirectCost, e.IndirectCost, e.MarkupCost, e.BidPrice,
        e.Sections.OrderBy(s => s.SortOrder).Select(s => new SectionBreakdown(
            s.Id, s.Code, s.Title, s.SortOrder, s.SectionTotal,
            s.Items.OrderBy(i => i.SortOrder).Select(i => new ItemBreakdown(
                i.Id, i.ItemCode, i.Description, i.Unit, i.Quantity, i.AssemblyId,
                i.UnitRate, i.LineTotal, i.SortOrder,
                BuildItemRate(i.CostComponents, typeMap).Lines, i.AreaId)).ToList())).ToList(),
        e.Preliminaries.OrderBy(p => p.SortOrder).Select(p => new PrelimBreakdown(
            p.Id, p.Description, p.Kind.ToString(), p.Amount, p.ComputedTotal, p.SortOrder)).ToList(),
        orderedMarkups.Select(m => new MarkupBreakdown(
            m.Id, m.Type.ToString(), m.Label, m.Percentage, m.ApplyOrder, m.ComputedAmount)).ToList(),
        rowVersion, fx);
}
