using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

// ── Breakdown DTOs returned to the client ─────────────────────────────────────
public record EstimateBreakdown(
    int Id, int ProjectId, int Revision, string Title, string Status, string Currency,
    decimal DirectCost, decimal IndirectCost, decimal MarkupCost, decimal BidPrice,
    decimal? TaxRatePct, decimal TaxAmount, decimal BidPriceInclTax, decimal AlternatesTotal,
    decimal MarginOnPricePct, decimal CommercialAdjustment,
    DateOnly? PricingDate,
    List<SectionBreakdown> Sections, List<PrelimBreakdown> Preliminaries, List<MarkupBreakdown> Markups,
    List<RiskBreakdown> Risks,
    decimal SuggestedContingencyAmount, decimal SuggestedContingencyPct,
    CashFlowProjection CashFlow,
    string RowVersion, FxView? Fx,
    /// <summary>25.5 — Percent change vs. the immediately-prior revision (N-1 by
    /// <see cref="Estimate.Revision"/>), or null when this is the first revision.
    /// Each per-card percent is null when the prior value was zero (div-by-0 guard).
    /// Estimators iterate prices across revisions; surfacing the direction +
    /// magnitude turns the wall of bid numbers into a story.</summary>
    PreviousRevisionDelta? PreviousDelta);

/// <summary>25.5 — Percent change of each cached total against the immediately-
/// prior revision in the same project (largest <see cref="Estimate.Revision"/>
/// number strictly less than this one — handles gaps from middle-revision
/// deletes). <c>Positive = current is HIGHER than previous</c>, regardless of
/// the sign of either value (we divide by |previous|, so a -100 → -50 move
/// reads as +50% — went up — rather than the -50% an unguarded ratio would
/// produce). The frontend decides which direction is "better" (cost cards =
/// lower; bid + markups = higher). Per-card percent is null when |previous|
/// was effectively zero (below half a cent), which prevents the "∞%" footgun
/// from a tiny-but-nonzero prior total; the frontend reads null as "card
/// first appeared on this revision" and shows a "NEW" pill instead.</summary>
public record PreviousRevisionDelta(
    int FromRevision,
    decimal? DirectCostPct,
    decimal? IndirectCostPct,
    decimal? MarkupCostPct,
    decimal? BidPricePct,
    decimal? BidPriceInclTaxPct);

/// <summary>One row of the risk register, plus its expected value (EV = p/100 × impact).</summary>
public record RiskBreakdown(int Id, string Title, string Category, decimal ProbabilityPct,
    decimal ImpactAmount, decimal ExpectedValue, string? Note, int SortOrder);

/// <summary>Cash-flow S-curve over the project duration. Months are 1-indexed; the
/// sum of <see cref="Monthly"/> equals <see cref="Total"/> exactly (rounding residue
/// absorbed in the last month).</summary>
public record CashFlowProjection(int DurationMonths, decimal Total, List<CashFlowMonth> Monthly);
public record CashFlowMonth(int Month, decimal Spend, decimal Cumulative);

/// <summary>The bid expressed in the estimate's secondary (presentation) currency.
/// <c>Rate</c> is the native→secondary factor; <c>Frozen</c> = snapshotted at publish.</summary>
public record FxView(string SecondaryCurrency, decimal Rate, decimal ConvertedBidPrice, bool Frozen, DateTime? FrozenAt);

public record SectionBreakdown(int Id, string Code, string Title, int SortOrder, decimal SectionTotal, List<ItemBreakdown> Items);
public record ItemBreakdown(int Id, string ItemCode, string Description, string Unit, decimal Quantity, int? AssemblyId, decimal UnitRate, decimal LineTotal, int SortOrder,
    List<ItemCostComponentBreakdown> Components, int? AreaId, string Kind);
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

// ── Target-price back-solve (commercial adjustment) ───────────────────────────
/// <summary>Result of solving the commercial adjustment to hit a target: the current
/// bid, the target bid, the existing adjustment, the adjustment required to land on
/// the target, and whether it was persisted.</summary>
public record TargetSolve(decimal CurrentBidPrice, decimal TargetBidPrice, decimal CurrentAdjustment, decimal RequiredAdjustment, bool Applied);

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
public class EstimateCalculator(AppDbContext db, RateEngine engine)
{
    public async Task<EstimateBreakdown?> RecomputeAsync(int estimateId)
    {
        var e = await LoadGraphAsync(estimateId);
        if (e is null) return null;
        var (ordered, typeMap) = await ComputeAsync(e);
        await db.SaveChangesAsync();
        // 25.5 — fetch N-1 AFTER SaveChangesAsync so we compare against committed
        // cached totals, not a possibly-stale tracked entity.
        var prevDelta = await ComputePreviousDeltaAsync(e);
        return Build(e, ordered, RowVersionOf(e), await BuildFxAsync(e), typeMap, prevDelta);
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
        .Include(x => x.Risks)
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
        // Resolve assembly rates for any assembly-priced items in one query. When the
        // estimate has a PricingDate set, recompute each used assembly's rate AS OF that
        // date via the rate-engine (which consults ResourceRateHistory). The live
        // Assembly.ComputedRate cache is left untouched — pricing-date is a per-estimate
        // lens, not a library mutation.
        var assemblyIds = e.Sections.SelectMany(s => s.Items)
                                    .Where(i => i.AssemblyId is not null)
                                    .Select(i => i.AssemblyId!.Value).Distinct().ToList();
        Dictionary<int, decimal> rates;
        if (e.PricingDate is { } pd)
        {
            rates = new Dictionary<int, decimal>();
            foreach (var aid in assemblyIds)
                rates[aid] = await engine.ComputeAssemblyRateAsAtAsync(aid, pd);
        }
        else
        {
            rates = await db.Assemblies.Where(a => assemblyIds.Contains(a.Id))
                                       .ToDictionaryAsync(a => a.Id, a => a.ComputedRate);
        }
        var typeMap = await db.CostComponentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t);

        // ── Direct cost: items → sections, bucketed by line kind ─────────────
        // Rate precedence: cost-component build-up → assembly rate → ad-hoc rate.
        //   markupable : Normal + Daywork — priced work the markups apply to
        //   excluded   : Provisional + PC sums — in the bid at net, NOT marked up
        //   alternates : Alternate — carried for the client's option, OUT of the bid
        // Section totals reflect the bid lines (markupable + excluded); alternates
        // are summed separately so they never inflate a section or the tender sum.
        decimal markupableDirect = 0m, excludedDirect = 0m, alternates = 0m;
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
                switch (i.Kind)
                {
                    case BoqItemKind.Alternate:
                        alternates += i.LineTotal;           // excluded from the bid
                        break;
                    case BoqItemKind.ProvisionalSum:
                    case BoqItemKind.PcSum:
                        excludedDirect += i.LineTotal;       // in the bid, not marked up
                        sectionTotal   += i.LineTotal;
                        break;
                    default:                                  // Normal, Daywork
                        markupableDirect += i.LineTotal;
                        sectionTotal     += i.LineTotal;
                        break;
                }
            }
            s.SectionTotal = EstimateMath.Round2(sectionTotal);
        }
        var direct = markupableDirect + excludedDirect;      // bid direct cost (excludes alternates)

        // ── Indirect cost: preliminaries ─────────────────────────────────────
        var durationMonths = e.Project.DurationMonths ?? 1;
        decimal indirect = 0m;
        foreach (var p in e.Preliminaries)
        {
            p.ComputedTotal = EstimateMath.PreliminaryTotal(p.Kind, p.Amount, durationMonths);
            indirect += p.ComputedTotal;
        }

        // ── Markups: compounding in ApplyOrder ───────────────────────────────
        // Markups compound only on the markupable direct cost + indirect — provisional/PC
        // sums pass through at net, so they are NOT in the markup base.
        var ordered = e.Markups.OrderBy(m => m.ApplyOrder).ToList();
        var (amounts, markupTotal, _) = EstimateMath.ApplyMarkups(
            markupableDirect + indirect, ordered.Select(m => m.Percentage).ToList());
        for (int k = 0; k < ordered.Count; k++) ordered[k].ComputedAmount = amounts[k];

        e.DirectCost      = EstimateMath.Round2(direct);
        e.IndirectCost    = EstimateMath.Round2(indirect);
        e.MarkupCost      = EstimateMath.Round2(markupTotal);
        // Bid = markupable + excluded (provisional/PC pass-through) + indirect + markups
        //     + the commercial adjustment (the final ± lump sum to land on a target).
        e.BidPrice        = EstimateMath.Round2(direct + indirect + markupTotal + e.CommercialAdjustment);
        e.AlternatesTotal = EstimateMath.Round2(alternates);
        // Tax sits OUTSIDE the markup cascade — computed on the finished (pre-tax) bid price.
        e.TaxAmount       = EstimateMath.Tax(e.BidPrice, e.TaxRatePct);
        e.UpdatedAt       = DateTime.UtcNow;

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

    /// <summary>
    /// Solve the commercial adjustment needed to land on a target — the final
    /// "we must be at AED X" move. The target may be an explicit ± lump sum, an
    /// absolute bid price, or a desired gross margin-on-price. Returns a non-persisting
    /// preview unless <paramref name="apply"/>, in which case the adjustment is saved and
    /// the estimate recomputed so its bid equals the target. Uses the cached cost totals.
    /// </summary>
    public async Task<TargetSolve?> SolveTargetAsync(int estimateId, decimal? targetPrice, decimal? targetMarginPct, decimal? explicitAdjustment, bool apply)
    {
        var e = await db.Estimates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == estimateId);
        if (e is null) return null;

        var bidExclAdj = e.BidPrice - e.CommercialAdjustment;   // the bid before any adjustment
        decimal targetBid, required;
        if (explicitAdjustment is { } adj) { required = EstimateMath.Round2(adj); targetBid = EstimateMath.Round2(bidExclAdj + required); }
        else if (targetPrice is { } tp)    { targetBid = EstimateMath.Round2(tp); required = EstimateMath.Round2(tp - bidExclAdj); }
        else if (targetMarginPct is { } m) { targetBid = EstimateMath.BidForTargetMargin(e.DirectCost + e.IndirectCost, m); required = EstimateMath.Round2(targetBid - bidExclAdj); }
        else return null;

        if (apply)
        {
            var tracked = await db.Estimates.FirstAsync(x => x.Id == estimateId);
            tracked.CommercialAdjustment = required;
            await db.SaveChangesAsync();
            await RecomputeAsync(estimateId);
        }
        return new TargetSolve(e.BidPrice, targetBid, e.CommercialAdjustment, required, apply);
    }

    /// <summary>Read-only breakdown without recomputing (uses cached values).</summary>
    public async Task<EstimateBreakdown?> GetAsync(int estimateId)
    {
        var e = await db.Estimates
            .Include(x => x.Project)
            .Include(x => x.Sections).ThenInclude(s => s.Items).ThenInclude(i => i.CostComponents)
            .Include(x => x.Preliminaries)
            .Include(x => x.Markups)
            .Include(x => x.Risks)
            .FirstOrDefaultAsync(x => x.Id == estimateId);
        if (e is null) return null;
        var typeMap = await db.CostComponentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t);
        var prevDelta = await ComputePreviousDeltaAsync(e);
        return Build(e, e.Markups.OrderBy(m => m.ApplyOrder).ToList(), RowVersionOf(e), await BuildFxAsync(e), typeMap, prevDelta);
    }

    /// <summary>25.5 — Compute the percent change of each cached total against
    /// the IMMEDIATELY-PRIOR revision in the same project (the largest Revision
    /// number strictly less than <paramref name="cur"/>.Revision). We use
    /// "largest &lt; cur" rather than strict "Revision − 1" so a deleted middle
    /// revision (project history {1, 3} after deleting Rev 2) still compares
    /// Rev 3 against Rev 1 — silently dropping the badge would tell the
    /// estimator "no prior" when one in fact exists. Superseded revisions are
    /// NOT skipped: they're part of the history and are what an estimator who
    /// looks at the current bid naturally compares to.
    ///
    /// Per-card percent is null when the prior value was effectively zero
    /// (below half a cent). The frontend reads null as "the card existed for
    /// the first time on this revision" and renders a "NEW" pill instead of
    /// a badge so the user can tell that case apart from "no prior revision".</summary>
    private async Task<PreviousRevisionDelta?> ComputePreviousDeltaAsync(Estimate cur)
    {
        if (cur.Revision <= 1) return null;
        // Lightweight projection — we only need the five cached totals, not the
        // full graph. The AppDbContext global query filter scopes this query to
        // the request's tenant automatically. ORDER BY Revision DESC + LIMIT 1
        // skips gaps caused by middle-revision deletes (see EstimateEndpoints
        // DELETE handler — no renumbering on delete).
        var prev = await db.Estimates
            .Where(x => x.ProjectId == cur.ProjectId && x.Revision < cur.Revision)
            .OrderByDescending(x => x.Revision)
            .Select(x => new
            {
                x.Revision,
                x.DirectCost,
                x.IndirectCost,
                x.MarkupCost,
                x.BidPrice,
                x.TaxAmount,
            })
            .FirstOrDefaultAsync();
        if (prev is null) return null;

        // 25.5 — Pct contract: positive = current is HIGHER than previous,
        // regardless of the sign of previous. Dividing by |previous| (not
        // `previous` directly) prevents a sign-flip when previous is negative
        // (BidPrice can go negative via a large negative CommercialAdjustment).
        // The "effectively zero" threshold (< 0.005, i.e. sub-cent at 2dp)
        // catches the tiny-prev → 999900% footgun the doc-comment promises
        // to prevent without false-positives on legitimately small totals.
        static decimal? Pct(decimal current, decimal previous)
        {
            var absPrev = Math.Abs(previous);
            return absPrev < 0.005m ? null : EstimateMath.Round2((current - previous) / absPrev * 100m);
        }

        return new PreviousRevisionDelta(
            prev.Revision,
            Pct(cur.DirectCost, prev.DirectCost),
            Pct(cur.IndirectCost, prev.IndirectCost),
            Pct(cur.MarkupCost, prev.MarkupCost),
            Pct(cur.BidPrice, prev.BidPrice),
            Pct(cur.BidPrice + cur.TaxAmount, prev.BidPrice + prev.TaxAmount));
    }

    private static EstimateBreakdown Build(Estimate e, List<Markup> orderedMarkups, string rowVersion, FxView? fx,
        IReadOnlyDictionary<int, CostComponentType> typeMap, PreviousRevisionDelta? previousDelta = null)
    {
        // ── Risk register → expected-value sum → suggested contingency (19.2) ──
        // EV per row = probability/100 × impact; the suggestion is EV / (direct+indirect)
        // as a percentage so it slots straight into the existing Contingency markup. The
        // suggestion is INFORMATIONAL — the estimator decides whether to apply it.
        var risks = e.Risks.OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .Select(r => new RiskBreakdown(r.Id, r.Title, r.Category.ToString(),
                r.ProbabilityPct, r.ImpactAmount,
                EstimateMath.ExpectedValue(r.ProbabilityPct, r.ImpactAmount),
                r.Note, r.SortOrder))
            .ToList();
        var suggestedEv = EstimateMath.Round2(risks.Sum(r => r.ExpectedValue));
        var cost = e.DirectCost + e.IndirectCost;
        var suggestedPct = cost > 0m
            ? EstimateMath.Round2(suggestedEv / cost * 100m)
            : 0m;

        // ── Cash-flow S-curve over the project duration ────────────────────────
        // Distributes the bid price (the cash that actually flows from the client
        // over the build) across DurationMonths using a closed-form Hermite S-curve.
        // Falls back to a single-month spike if duration is unset or 1.
        var durationMonths = Math.Max(1, e.Project?.DurationMonths ?? 1);
        var monthlySpend = EstimateMath.SCurveMonthlySpend(e.BidPrice, durationMonths);
        var cashflowMonths = new List<CashFlowMonth>(durationMonths);
        decimal running = 0m;
        for (int m = 0; m < monthlySpend.Length; m++)
        {
            running = EstimateMath.Round2(running + monthlySpend[m]);
            cashflowMonths.Add(new CashFlowMonth(m + 1, monthlySpend[m], running));
        }
        var cashflow = new CashFlowProjection(durationMonths, EstimateMath.Round2(e.BidPrice), cashflowMonths);

        return new(
            e.Id, e.ProjectId, e.Revision, e.Title, e.Status.ToString(), e.Currency,
            e.DirectCost, e.IndirectCost, e.MarkupCost, e.BidPrice,
            e.TaxRatePct, e.TaxAmount, EstimateMath.Round2(e.BidPrice + e.TaxAmount), e.AlternatesTotal,
            // Gross margin as a % of the (pre-tax) selling price: (BidPrice − cost) / BidPrice,
            // where cost = direct + indirect. The numerator is the markups PLUS any commercial
            // adjustment. Surfacing it guards against the markup-on-cost vs margin-on-price error.
            e.BidPrice > 0m ? EstimateMath.Round2((e.BidPrice - e.DirectCost - e.IndirectCost) / e.BidPrice * 100m) : 0m,
            e.CommercialAdjustment,
            e.PricingDate,
            e.Sections.OrderBy(s => s.SortOrder).Select(s => new SectionBreakdown(
                s.Id, s.Code, s.Title, s.SortOrder, s.SectionTotal,
                s.Items.OrderBy(i => i.SortOrder).Select(i => new ItemBreakdown(
                    i.Id, i.ItemCode, i.Description, i.Unit, i.Quantity, i.AssemblyId,
                    i.UnitRate, i.LineTotal, i.SortOrder,
                    BuildItemRate(i.CostComponents, typeMap).Lines, i.AreaId, i.Kind.ToString())).ToList())).ToList(),
            e.Preliminaries.OrderBy(p => p.SortOrder).Select(p => new PrelimBreakdown(
                p.Id, p.Description, p.Kind.ToString(), p.Amount, p.ComputedTotal, p.SortOrder)).ToList(),
            orderedMarkups.Select(m => new MarkupBreakdown(
                m.Id, m.Type.ToString(), m.Label, m.Percentage, m.ApplyOrder, m.ComputedAmount)).ToList(),
            risks,
            suggestedEv, suggestedPct,
            cashflow,
            rowVersion, fx,
            previousDelta);
    }
}
