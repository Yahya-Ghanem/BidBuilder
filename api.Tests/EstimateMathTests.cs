using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using Xunit;

namespace BidBuilder.Api.Tests;

public class EstimateMathTests
{
    // ── Line totals ──────────────────────────────────────────────────────────
    [Theory]
    [InlineData(250, 124.50, 31125.00)]   // RC footing example: 250 m³ × 124.50
    [InlineData(0, 99.99, 0.00)]
    [InlineData(1.5, 10.00, 15.00)]
    public void LineTotal_multiplies_and_rounds(decimal qty, decimal rate, decimal expected)
        => Assert.Equal(expected, EstimateMath.LineTotal(qty, rate));

    [Fact]
    public void LineTotal_rounds_half_away_from_zero()
        => Assert.Equal(0.13m, EstimateMath.LineTotal(1, 0.125m));

    // ── Preliminaries ────────────────────────────────────────────────────────
    [Fact]
    public void Fixed_preliminary_ignores_duration()
        => Assert.Equal(5000.00m, EstimateMath.PreliminaryTotal(PreliminaryKind.Fixed, 5000m, 9));

    [Fact]
    public void TimeRelated_preliminary_multiplies_by_months()
        => Assert.Equal(27000.00m, EstimateMath.PreliminaryTotal(PreliminaryKind.TimeRelated, 3000m, 9));

    [Fact]
    public void TimeRelated_preliminary_floors_duration_at_one()
        => Assert.Equal(3000.00m, EstimateMath.PreliminaryTotal(PreliminaryKind.TimeRelated, 3000m, 0));

    // ── Markups (compounding in order) ─────────────────────────────────────────
    [Fact]
    public void Markups_compound_in_sequence()
    {
        // base 100,000 → overhead 8% (8,000) → profit 12% on 108,000 (12,960)
        //              → contingency 5% on 120,960 (6,048)
        var (amounts, total, final) = EstimateMath.ApplyMarkups(100_000m, new[] { 8m, 12m, 5m });

        Assert.Equal(8_000.00m,  amounts[0]);
        Assert.Equal(12_960.00m, amounts[1]);
        Assert.Equal(6_048.00m,  amounts[2]);
        Assert.Equal(27_008.00m, total);
        Assert.Equal(127_008.00m, final);
    }

    // ── Tax / VAT (applied after markups, outside the cascade) ─────────────────
    [Theory]
    [InlineData(100_000, 5, 5_000.00)]
    [InlineData(73_853.44, 5, 3_692.67)]   // half-away rounding of 3,692.672
    [InlineData(1_000, 0, 0.00)]
    public void Tax_applies_rate_to_bid_price(decimal bid, decimal rate, decimal expected)
        => Assert.Equal(expected, EstimateMath.Tax(bid, rate));

    [Fact]
    public void Tax_is_zero_for_null_or_nonpositive_rate()
    {
        Assert.Equal(0m, EstimateMath.Tax(1_000m, null));
        Assert.Equal(0m, EstimateMath.Tax(1_000m, 0m));
        Assert.Equal(0m, EstimateMath.Tax(1_000m, -5m));
    }

    [Fact]
    public void No_markups_leaves_base_untouched()
    {
        var (amounts, total, final) = EstimateMath.ApplyMarkups(50_000m, System.Array.Empty<decimal>());
        Assert.Empty(amounts);
        Assert.Equal(0m, total);
        Assert.Equal(50_000m, final);
    }

    [Fact]
    public void Full_buildup_direct_indirect_markups()
    {
        // 2 items: 250×124.50 + 100×80 = 31,125 + 8,000 = 39,125 direct
        var direct = EstimateMath.LineTotal(250, 124.50m) + EstimateMath.LineTotal(100, 80m);
        Assert.Equal(39_125.00m, direct);

        // prelims: fixed 5,000 + time-related 2,000/mo × 6 = 12,000 → 17,000 indirect
        var indirect = EstimateMath.PreliminaryTotal(PreliminaryKind.Fixed, 5_000m, 6)
                     + EstimateMath.PreliminaryTotal(PreliminaryKind.TimeRelated, 2_000m, 6);
        Assert.Equal(17_000.00m, indirect);

        // markups 10% then 5% compounding on 56,125
        // 10% of 56,125 = 5,612.50 → running 61,737.50; 5% of that = 3,086.88
        var (_, markupTotal, bid) = EstimateMath.ApplyMarkups(direct + indirect, new[] { 10m, 5m });
        Assert.Equal(8_699.38m, markupTotal);     // 5,612.50 + 3,086.88
        Assert.Equal(64_824.38m, bid);
    }
}
