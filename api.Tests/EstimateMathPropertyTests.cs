using System.Linq;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// Property-based checks over the pure estimating arithmetic. Inputs are drawn from a
/// SEEDED generator, so the suite is exhaustive-feeling yet fully deterministic and
/// reproducible — a failure reproduces on every machine/CI run, never flakes. Each
/// test asserts an invariant that must hold for ALL well-formed inputs, catching the
/// rounding/ordering regressions a handful of example tests would miss.
/// </summary>
public class EstimateMathPropertyTests
{
    private static decimal Money(System.Random r, double max) =>
        System.Math.Round((decimal)(r.NextDouble() * max), 4, System.MidpointRounding.AwayFromZero);

    // ── Round2 is idempotent: rounding an already-rounded value changes nothing. ──
    [Fact]
    public void Round2_is_idempotent()
    {
        var r = new System.Random(1);
        for (int i = 0; i < 1000; i++)
        {
            var x = Money(r, 1_000_000);
            Assert.Equal(EstimateMath.Round2(x), EstimateMath.Round2(EstimateMath.Round2(x)));
        }
    }

    // ── LineTotal is non-negative for non-negative inputs and never drifts more ──
    //    than half a cent from the exact product (it is just a 2-dp rounding of it).
    [Fact]
    public void LineTotal_nonnegative_and_within_half_a_cent()
    {
        var r = new System.Random(2);
        for (int i = 0; i < 1000; i++)
        {
            var q = Money(r, 5_000);
            var rate = Money(r, 5_000);
            var lt = EstimateMath.LineTotal(q, rate);
            Assert.True(lt >= 0m);
            Assert.True(System.Math.Abs(lt - q * rate) <= 0.005m);
        }
    }

    // ── LineTotal is monotonic in quantity (more quantity never costs less). ──────
    [Fact]
    public void LineTotal_is_monotonic_in_quantity()
    {
        var r = new System.Random(3);
        for (int i = 0; i < 500; i++)
        {
            var rate = Money(r, 5_000);
            var q1 = Money(r, 2_500);
            var q2 = q1 + Money(r, 2_500);   // q2 >= q1
            Assert.True(EstimateMath.LineTotal(q1, rate) <= EstimateMath.LineTotal(q2, rate));
        }
    }

    // ── A time-related preliminary floors the duration at one month and is never ──
    //    cheaper than the same amount taken as a one-off fixed line.
    [Fact]
    public void TimeRelated_preliminary_floors_at_one_month_and_dominates_fixed()
    {
        var r = new System.Random(4);
        for (int i = 0; i < 500; i++)
        {
            var amount = Money(r, 50_000);
            Assert.Equal(EstimateMath.PreliminaryTotal(PreliminaryKind.TimeRelated, amount, 1),
                         EstimateMath.PreliminaryTotal(PreliminaryKind.TimeRelated, amount, 0));   // 0 → floored to 1
            Assert.Equal(EstimateMath.PreliminaryTotal(PreliminaryKind.TimeRelated, amount, 1),
                         EstimateMath.PreliminaryTotal(PreliminaryKind.TimeRelated, amount, -5));  // negative → floored to 1
            var months = r.Next(1, 36);
            Assert.True(EstimateMath.PreliminaryTotal(PreliminaryKind.TimeRelated, amount, months)
                     >= EstimateMath.PreliminaryTotal(PreliminaryKind.Fixed, amount, months));
        }
    }

    // ── Markup build-up invariants: the per-line amounts sum to the reported total,
    //    the final subtotal is exactly base + total, markups only ever add value, and
    //    every line amount is non-negative for non-negative inputs.
    [Fact]
    public void ApplyMarkups_invariants_hold_for_all_inputs()
    {
        var r = new System.Random(5);
        for (int t = 0; t < 1000; t++)
        {
            var baseAmount = Money(r, 1_000_000);
            var n = r.Next(0, 6);
            var pcts = new decimal[n];
            for (int k = 0; k < n; k++) pcts[k] = Money(r, 50);   // 0–50% per line

            var (amounts, total, final) = EstimateMath.ApplyMarkups(baseAmount, pcts);

            Assert.Equal(n, amounts.Length);             // one amount per percentage
            Assert.Equal(total, amounts.Sum());          // amounts reconcile to the total
            Assert.Equal(final, baseAmount + total);     // final subtotal = base + markups
            Assert.True(final >= baseAmount);            // markups only add
            Assert.All(amounts, a => Assert.True(a >= 0m));
        }
    }

    // ── Zero percentages add nothing, regardless of how many or their order. ──────
    [Fact]
    public void Zero_percent_markups_never_change_the_base()
    {
        var r = new System.Random(6);
        for (int i = 0; i < 500; i++)
        {
            var baseAmount = Money(r, 1_000_000);
            var zeros = new decimal[r.Next(0, 6)];   // all default 0m
            var (_, total, final) = EstimateMath.ApplyMarkups(baseAmount, zeros);
            Assert.Equal(0m, total);
            Assert.Equal(baseAmount, final);
        }
    }
}
