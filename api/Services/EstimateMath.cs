using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// Pure estimating arithmetic — no DB, no HTTP, fully unit-testable. This is the
/// crown-jewel logic that turns quantities + rates into a bid price, so it lives
/// apart from orchestration and is covered by tests.
///
/// Build-up: direct cost (Σ qty×rate) + indirect (preliminaries) then markups
/// applied in sequence, each compounding on the running subtotal — standard
/// tender practice (profit is taken on overhead, contingency on both, etc.).
/// </summary>
public static class EstimateMath
{
    public static decimal Round2(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    /// <summary>Line total for a BOQ item.</summary>
    public static decimal LineTotal(decimal quantity, decimal unitRate) => Round2(quantity * unitRate);

    /// <summary>Resolve a preliminary's contribution. Time-related lines multiply
    /// by the project duration (months, min 1); fixed lines are one-off.</summary>
    public static decimal PreliminaryTotal(PreliminaryKind kind, decimal amount, int durationMonths) =>
        Round2(kind == PreliminaryKind.TimeRelated ? amount * Math.Max(1, durationMonths) : amount);

    /// <summary>Sales tax / VAT on the (post-markup) bid price. A null or non-positive
    /// rate yields zero — tax is applied AFTER markups and never compounds into them.</summary>
    public static decimal Tax(decimal bidPrice, decimal? ratePct) =>
        ratePct is { } r && r > 0m ? Round2(bidPrice * r / 100m) : 0m;

    /// <summary>The bid price that yields a desired gross margin on price:
    /// price = cost / (1 − margin%). A margin ≥ 100% is impossible → 0.</summary>
    public static decimal BidForTargetMargin(decimal cost, decimal targetMarginPct) =>
        targetMarginPct >= 100m ? 0m : Round2(cost / (1m - targetMarginPct / 100m));

    /// <summary>
    /// Apply markup percentages in the given order, compounding on the running
    /// subtotal. Returns the per-markup amounts (aligned to input order), their
    /// sum, and the final subtotal (= bid price when seeded with direct+indirect).
    /// </summary>
    public static (decimal[] Amounts, decimal Total, decimal FinalSubtotal) ApplyMarkups(
        decimal baseAmount, IReadOnlyList<decimal> percentagesInOrder)
    {
        var amounts = new decimal[percentagesInOrder.Count];
        var running = baseAmount;
        decimal total = 0m;
        for (int i = 0; i < percentagesInOrder.Count; i++)
        {
            var amt = Round2(running * percentagesInOrder[i] / 100m);
            amounts[i] = amt;
            running += amt;
            total += amt;
        }
        return (amounts, total, running);
    }

    /// <summary>Risk Expected Value: probability/100 × impact, never negative.</summary>
    public static decimal ExpectedValue(decimal probabilityPct, decimal impactAmount) =>
        probabilityPct <= 0m || impactAmount <= 0m
            ? 0m
            : Round2(probabilityPct / 100m * impactAmount);

    /// <summary>
    /// S-curve cumulative spend at fractional time t ∈ [0,1]. Uses the Hermite
    /// smoothstep <c>F(t) = t²(3 − 2t)</c> — a closed-form, monotonic,
    /// symmetric-around-50% S-shape that integrates beautifully and needs no
    /// numeric tables. Construction projects rarely deviate enough from this
    /// shape to justify a richer model in v1 (custom curves can come later).
    /// F(0) = 0, F(0.5) = 0.5, F(1) = 1.
    /// </summary>
    public static decimal SCurveCumulative(decimal t)
    {
        if (t <= 0m) return 0m;
        if (t >= 1m) return 1m;
        return t * t * (3m - 2m * t);
    }

    /// <summary>
    /// Distribute a total spend across <paramref name="durationMonths"/> months
    /// following the S-curve. Returns the per-month spend (length == durationMonths,
    /// rounded to 2dp). Any rounding residue is absorbed into the LAST month so the
    /// sum matches <paramref name="totalSpend"/> exactly. Returns a single-element
    /// list for duration ≤ 1.
    /// </summary>
    public static decimal[] SCurveMonthlySpend(decimal totalSpend, int durationMonths)
    {
        var n = Math.Max(1, durationMonths);
        if (n == 1) return new[] { Round2(totalSpend) };
        var monthly = new decimal[n];
        decimal running = 0m;
        for (int m = 1; m <= n; m++)
        {
            var t = (decimal)m / n;
            var cumulative = Round2(SCurveCumulative(t) * totalSpend);
            monthly[m - 1] = cumulative - running;
            running = cumulative;
        }
        // Force the sum to equal totalSpend by absorbing any rounding drift in the last month.
        var residue = Round2(totalSpend) - monthly.Sum();
        monthly[n - 1] += residue;
        return monthly;
    }
}
