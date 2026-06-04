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
}
