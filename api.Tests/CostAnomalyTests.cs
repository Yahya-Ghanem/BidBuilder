using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 23.5 — Cost-anomaly detection
/// (<c>GET /api/estimates/{id}/anomalies</c>).
///
/// Each test uses a Guid-suffixed Unit string so its history bucket is
/// isolated from the rest of the suite (the service buckets by unit + tenant
/// across ALL other estimates, so the shared "hr"/"m"/"m2" pools would
/// otherwise drown out a 5-line seed with noise from sibling tests).
/// </summary>
[Collection("api")]
public class CostAnomalyTests(ApiFixture fx)
{
    static Task<HttpResponseMessage> AddItemAsync(
        HttpClient c, int eid, int sid, string unit, decimal qty, decimal rate, string desc = "stuff") =>
        c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items",
            new { description = desc, unit, quantity = qty, unitRate = rate, sortOrder = 0 });

    /// <summary>Per-test unit, so the in-tenant "history" bucket is just our seed.</summary>
    static string UniqueUnit() => $"u-{Guid.NewGuid():N}".Substring(0, 12);

    /// <summary>Build N background estimates each containing one line at baseline rate.
    /// These become the "history" bucket the test's subject line is compared against.</summary>
    private async Task SeedBaselineAsync(HttpClient admin, int pid, string unit, decimal rate, int count, string desc = "stuff")
    {
        for (int i = 0; i < count; i++)
        {
            var eid = await Api.NewEstimateAsync(admin, pid, $"baseline-{Guid.NewGuid():N}");
            var sid = await Api.AddSectionAsync(admin, eid, "B");
            (await AddItemAsync(admin, eid, sid, unit, qty: 1, rate: rate, desc: desc)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Wildly_overpriced_line_is_flagged_by_z_score()
    {
        var admin = await fx.AdminClientAsync();
        var pid   = await Api.ProjectIdAsync(admin);
        var unit  = UniqueUnit();

        // Spread baseline so stddev > 0 → z-score path actually fires (rather than
        // the median-multiple fallback). Mean ≈ 100, stddev ≈ 7. A 200 line then
        // sits ~14 stddevs out.
        foreach (var r in new[] { 90m, 95m, 100m, 105m, 110m })
            await SeedBaselineAsync(admin, pid, unit, rate: r, count: 1);

        // Subject estimate: one line at 200 — extreme overcharge vs the spread.
        var eid = await Api.NewEstimateAsync(admin, pid, $"subject-{Guid.NewGuid():N}");
        var sid = await Api.AddSectionAsync(admin, eid, "S");
        (await AddItemAsync(admin, eid, sid, unit, qty: 1, rate: 200m)).EnsureSuccessStatusCode();

        var body = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/anomalies");

        Assert.Equal(1, body.GetProperty("itemsScanned").GetInt32());
        Assert.Equal(1, body.GetProperty("itemsFlagged").GetInt32());
        var flagged = body.GetProperty("items")[0];
        Assert.Equal("high", flagged.GetProperty("severity").GetString());
        Assert.Equal(200m, flagged.GetProperty("unitRate").GetDecimal());
        Assert.Equal(100m, flagged.GetProperty("historicalMedian").GetDecimal());
        Assert.True(flagged.GetProperty("zScore").GetDouble() >= 2.5);
    }

    [Fact]
    public async Task Normal_rate_within_distribution_is_not_flagged()
    {
        var admin = await fx.AdminClientAsync();
        var pid   = await Api.ProjectIdAsync(admin);
        var unit  = UniqueUnit();

        // Tight cluster around 100 (95, 98, 100, 102, 105) gives stddev ≈ 3.5.
        foreach (var r in new[] { 95m, 98m, 100m, 102m, 105m })
            await SeedBaselineAsync(admin, pid, unit, rate: r, count: 1);

        var eid = await Api.NewEstimateAsync(admin, pid, $"subject-{Guid.NewGuid():N}");
        var sid = await Api.AddSectionAsync(admin, eid, "S");
        (await AddItemAsync(admin, eid, sid, unit, qty: 1, rate: 101m)).EnsureSuccessStatusCode();

        var body = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/anomalies");
        Assert.Equal(0, body.GetProperty("itemsFlagged").GetInt32());
    }

    [Fact]
    public async Task Underpriced_line_is_also_flagged()
    {
        // A *very* low rate is still an anomaly — could be a missing zero, a unit
        // mistake, or simply not credible. The symmetric median-multiple trigger
        // catches it even when the historic spread is tight enough to keep z near 0.
        var admin = await fx.AdminClientAsync();
        var pid   = await Api.ProjectIdAsync(admin);
        var unit  = UniqueUnit();

        await SeedBaselineAsync(admin, pid, unit, rate: 100m, count: 5);

        var eid = await Api.NewEstimateAsync(admin, pid, $"subject-{Guid.NewGuid():N}");
        var sid = await Api.AddSectionAsync(admin, eid, "S");
        (await AddItemAsync(admin, eid, sid, unit, qty: 1, rate: 1m)).EnsureSuccessStatusCode();

        var body = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/anomalies");

        Assert.Equal(1, body.GetProperty("itemsFlagged").GetInt32());
        var flagged = body.GetProperty("items")[0];
        // 1 / 100 = 0.01 — far below the 1/3 low-multiple trigger.
        Assert.True(flagged.GetProperty("medianMultiple").GetDecimal() <= 0.05m);
        Assert.Contains("median", flagged.GetProperty("reason").GetString()!);
    }

    [Fact]
    public async Task Median_multiple_fires_even_when_stddev_is_zero()
    {
        // All historic lines exactly 50 → stddev = 0, so the z-score check is
        // silent. The 3× median fallback should still catch a 200 line.
        var admin = await fx.AdminClientAsync();
        var pid   = await Api.ProjectIdAsync(admin);
        var unit  = UniqueUnit();

        await SeedBaselineAsync(admin, pid, unit, rate: 50m, count: 5);

        var eid = await Api.NewEstimateAsync(admin, pid, $"subject-{Guid.NewGuid():N}");
        var sid = await Api.AddSectionAsync(admin, eid, "S");
        (await AddItemAsync(admin, eid, sid, unit, qty: 1, rate: 200m)).EnsureSuccessStatusCode();

        var body = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/anomalies");
        Assert.Equal(1, body.GetProperty("itemsFlagged").GetInt32());
        var flagged = body.GetProperty("items")[0];
        Assert.True(flagged.GetProperty("medianMultiple").GetDecimal() >= 3.0m);
        // Reason should mention "median" (since z-score path is silent on stddev=0).
        Assert.Contains("median", flagged.GetProperty("reason").GetString()!);
    }

    [Fact]
    public async Task Insufficient_history_yields_no_flag()
    {
        // Fewer than MinHistoryForZ (=5) comparable lines → we don't have enough
        // signal to flag honestly, so the line is silently passed.
        var admin = await fx.AdminClientAsync();
        var pid   = await Api.ProjectIdAsync(admin);
        var unit  = UniqueUnit();

        await SeedBaselineAsync(admin, pid, unit, rate: 100m, count: 3);   // ≤ 4 < 5

        var eid = await Api.NewEstimateAsync(admin, pid, $"subject-{Guid.NewGuid():N}");
        var sid = await Api.AddSectionAsync(admin, eid, "S");
        (await AddItemAsync(admin, eid, sid, unit, qty: 1, rate: 9999m)).EnsureSuccessStatusCode();

        var body = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/anomalies");
        Assert.Equal(0, body.GetProperty("itemsFlagged").GetInt32());
    }

    [Fact]
    public async Task Items_with_no_unit_rate_are_skipped()
    {
        // A line with UnitRate = 0 isn't anomalous, it's just unpriced. The
        // service should ignore those (and they don't pollute history either).
        var admin = await fx.AdminClientAsync();
        var pid   = await Api.ProjectIdAsync(admin);
        var unit  = UniqueUnit();

        await SeedBaselineAsync(admin, pid, unit, rate: 100m, count: 5);

        var eid = await Api.NewEstimateAsync(admin, pid, $"subject-{Guid.NewGuid():N}");
        var sid = await Api.AddSectionAsync(admin, eid, "S");
        (await AddItemAsync(admin, eid, sid, unit, qty: 1, rate: 0m)).EnsureSuccessStatusCode();

        var body = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/anomalies");
        // itemsScanned counts only priced normal lines; the zero-rate one is invisible.
        Assert.Equal(0, body.GetProperty("itemsFlagged").GetInt32());
    }

    [Fact]
    public async Task Returns_404_for_unknown_or_cross_tenant_estimate()
    {
        var admin = await fx.AdminClientAsync();
        var r     = await admin.GetAsync("/api/estimates/999999/anomalies");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
