using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 23.4 — Rate trend (sparkline + volatility) for a library resource. Proves that:
/// the endpoint averages dated history per month, carries forward the last seen rate
/// across empty months, picks up the live rate as the latest point, and computes
/// 12-month min/max + a stddev/mean volatility index.
/// </summary>
[Collection("api")]
public class RateTrendTests(ApiFixture fx)
{
    /// <summary>Create a fresh Labor resource with a known live rate and return its id.</summary>
    private async Task<int> SeedLaborAsync(HttpClient admin, decimal liveRatePerHour)
    {
        var code = $"L-RT-{Guid.NewGuid():N}";
        var resp = await admin.PostAsJsonAsync("/api/resources/labor/", new
        {
            code, name = "Trend tester", unit = "hr",
            ratePerHour = liveRatePerHour, isActive = true,
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Json()).GetProperty("id").GetInt32();
    }

    /// <summary>Insert a back-dated rate point directly in the DB so tests can seed
    /// multi-month history without round-tripping through the cascade.</summary>
    private async Task SeedHistoryAsync(int resourceId, DateOnly effectiveFrom, decimal rate)
    {
        using var scope = await fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ResourceRateHistory.Add(new ResourceRateHistory
        {
            ResourceType = ResourceType.Labor, ResourceId = resourceId,
            EffectiveFrom = effectiveFrom, Rate = rate, Source = "test seed",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task No_history_returns_an_empty_series_with_the_live_rate_as_current()
    {
        var admin = await fx.AdminClientAsync();
        var id = await SeedLaborAsync(admin, 75m);

        var body = await (await admin.GetAsync($"/api/resources/labor/{id}/rate-trend")).Json();

        Assert.Equal("Labor", body.GetProperty("resourceType").GetString());
        Assert.Equal(75m, body.GetProperty("currentRate").GetDecimal());
        Assert.Empty(body.GetProperty("points").EnumerateArray());
    }

    [Fact]
    public async Task Multiple_history_points_in_one_month_are_averaged()
    {
        var admin = await fx.AdminClientAsync();
        var id = await SeedLaborAsync(admin, 60m);
        var thisMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-6);
        // Three points within the same month — the trend should report their mean (60).
        await SeedHistoryAsync(id, thisMonth.AddDays(1),  50m);
        await SeedHistoryAsync(id, thisMonth.AddDays(15), 60m);
        await SeedHistoryAsync(id, thisMonth.AddDays(28), 70m);

        var body = await (await admin.GetAsync($"/api/resources/labor/{id}/rate-trend")).Json();
        var points = body.GetProperty("points").EnumerateArray()
            .Select(p => (Month: p.GetProperty("month").GetString(), Rate: p.GetProperty("rate").GetDecimal()))
            .ToList();
        var match = points.First(p => p.Month == $"{thisMonth.Year:D4}-{thisMonth.Month:D2}");
        Assert.Equal(60m, match.Rate);
    }

    [Fact]
    public async Task Empty_months_carry_forward_the_last_seen_rate()
    {
        // Point at month -5 only; the chart should show that rate continuously through
        // month -1 and then transition to the live rate at the current month.
        var admin = await fx.AdminClientAsync();
        var id = await SeedLaborAsync(admin, 100m);
        var thisMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        await SeedHistoryAsync(id, thisMonth.AddMonths(-5).AddDays(2), 80m);

        var body = await (await admin.GetAsync($"/api/resources/labor/{id}/rate-trend")).Json();
        var points = body.GetProperty("points").EnumerateArray()
            .Select(p => (Month: p.GetProperty("month").GetString()!, Rate: p.GetProperty("rate").GetDecimal()))
            .ToDictionary(t => t.Month, t => t.Rate);

        // Months -4, -3, -2, -1 all carry the 80 rate forward.
        for (var k = 1; k <= 4; k++)
        {
            var m = thisMonth.AddMonths(-k);
            var key = $"{m.Year:D4}-{m.Month:D2}";
            Assert.Equal(80m, points[key]);
        }
        // The final point reflects the live rate (100), not the carried-forward 80.
        var thisKey = $"{thisMonth.Year:D4}-{thisMonth.Month:D2}";
        Assert.Equal(100m, points[thisKey]);
    }

    [Fact]
    public async Task Volatility_index_is_zero_for_a_flat_series_and_nonzero_when_it_swings()
    {
        var admin = await fx.AdminClientAsync();

        // FLAT: every month at 100 (carry-forward + live), volatility ≈ 0.
        var flatId = await SeedLaborAsync(admin, 100m);
        await SeedHistoryAsync(flatId, new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-18), 100m);
        var flatBody = await (await admin.GetAsync($"/api/resources/labor/{flatId}/rate-trend")).Json();
        var flatVol = flatBody.GetProperty("volatilityIndex").GetDecimal();
        Assert.True(flatVol < 0.001m, $"expected flat series to have ~0 volatility, got {flatVol}");

        // SWINGY: alternating 50/200 month-by-month → very high coefficient of variation.
        var swingId = await SeedLaborAsync(admin, 200m);
        var anchor = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        for (var k = 18; k >= 0; k--)
            await SeedHistoryAsync(swingId, anchor.AddMonths(-k).AddDays(1), k % 2 == 0 ? 50m : 200m);
        var swingBody = await (await admin.GetAsync($"/api/resources/labor/{swingId}/rate-trend")).Json();
        Assert.True(swingBody.GetProperty("volatilityIndex").GetDecimal() > 0.3m);
    }

    [Fact]
    public async Task Twelve_month_min_max_excludes_history_overwritten_before_the_window()
    {
        // Carry-forward intentionally extends an old rate until an update overwrites it.
        // To prove "history older than 12 months doesn't taint the 12m window" we have to
        // overwrite the old rate at (or before) the start of the window, so the carry-
        // forward at month -11 reflects the NEWER value — not the ancient outlier.
        var admin = await fx.AdminClientAsync();
        var id = await SeedLaborAsync(admin, 100m);
        var anchor = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        await SeedHistoryAsync(id, anchor.AddMonths(-20).AddDays(1), 999m);   // ancient outlier
        await SeedHistoryAsync(id, anchor.AddMonths(-11).AddDays(1), 110m);   // start of 12m window
        await SeedHistoryAsync(id, anchor.AddMonths(-6).AddDays(1),  100m);   // matches live rate

        var body = await (await admin.GetAsync($"/api/resources/labor/{id}/rate-trend")).Json();
        var min = body.GetProperty("min12m").GetDecimal();
        var max = body.GetProperty("max12m").GetDecimal();
        Assert.True(max < 999m, "outlier from before the window must not survive once overwritten at the boundary");
        Assert.Equal(100m, min);
        Assert.Equal(110m, max);
    }

    [Fact]
    public async Task Unknown_resource_returns_404()
    {
        var admin = await fx.AdminClientAsync();
        var resp = await admin.GetAsync("/api/resources/labor/999999999/rate-trend");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Bad_resource_type_returns_400()
    {
        var admin = await fx.AdminClientAsync();
        var resp = await admin.GetAsync("/api/resources/widgets/1/rate-trend");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
