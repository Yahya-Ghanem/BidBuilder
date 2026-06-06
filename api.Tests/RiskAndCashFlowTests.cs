using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Services;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 19.2 coverage. The risk register + cash-flow S-curve must:
///   1. Sum EVs correctly (p/100 × impact per row).
///   2. Suggest a contingency % defensibly (EV ÷ (direct+indirect)).
///   3. Apply that suggestion onto the Contingency markup on demand,
///      creating one if absent, never doubling it on re-apply.
///   4. Distribute the bid across <c>DurationMonths</c> with a closed-form
///      Hermite S-curve whose monthly slices sum EXACTLY to the bid (no
///      rounding drift left on the table).
/// </summary>
[Collection("api")]
public class RiskAndCashFlowTests(ApiFixture fx)
{
    [Fact]
    public void SCurve_monthly_sum_equals_total_for_a_variety_of_durations()
    {
        // For each plausible duration the monthly slices must sum back to the input total
        // exactly — any rounding residue is absorbed in the last month. The Hermite
        // smoothstep is monotonic, so cumulative spend is also non-decreasing.
        var rnd = new System.Random(19_02_19);
        for (int trial = 0; trial < 20; trial++)
        {
            var duration = rnd.Next(1, 37);              // 1 to 36 months
            var totalCents = rnd.Next(100, 10_000_000);  // up to USD 100k
            var total = totalCents / 100m;

            var monthly = EstimateMath.SCurveMonthlySpend(total, duration);
            Assert.Equal(duration, monthly.Length);
            Assert.Equal(total, monthly.Sum());

            // Cumulative is monotonic non-decreasing.
            decimal running = 0m, prev = 0m;
            for (int i = 0; i < monthly.Length; i++)
            {
                running += monthly[i];
                Assert.True(running >= prev, $"non-monotonic at month {i + 1} (duration={duration})");
                prev = running;
            }
        }
    }

    [Fact]
    public void SCurve_at_midpoint_is_close_to_half_the_total_for_even_durations()
    {
        // Property of F(t) = t²(3 − 2t): F(0.5) = 0.5 exactly.
        var monthly = EstimateMath.SCurveMonthlySpend(100m, 4);
        // After 2/4 months ~ 50% of 100 should be roughly spent. The discretisation
        // loses some accuracy but the cumulative at the midpoint is in a tight band.
        var cumulativeAtHalf = monthly[0] + monthly[1];
        Assert.InRange(cumulativeAtHalf, 45m, 55m);
    }

    [Fact]
    public async Task Risk_register_round_trip_computes_EV_and_suggested_contingency()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "RiskRegisterTest");
        // Seed direct + indirect cost so the suggested % has a denominator.
        //   Section A: 1 line × 10,000 = 10,000 direct cost
        //   Preliminary: 5,000 fixed → 5,000 indirect cost
        // → cost = 15,000
        var sid = await Api.AddSectionAsync(admin, eid, $"S-{System.Guid.NewGuid():N}"[..6]);
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Direct work", unit = "ls", quantity = 1m, assemblyId = (int?)null,
            unitRate = 10_000m, sortOrder = 1,
        })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/preliminaries", new
        {
            description = "Site office", kind = "Fixed", amount = 5_000m, sortOrder = 1,
        })).EnsureSuccessStatusCode();

        // Two risks: 50% × 1000 = 500, 25% × 4000 = 1000 → EV total = 1500
        // suggestedPct = 1500 / 15000 × 100 = 10.00%
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/risks", new
        {
            title = "Permit delay", category = "Schedule", probabilityPct = 50m,
            impactAmount = 1_000m, note = "depends on EIA outcome", sortOrder = 1,
        })).EnsureSuccessStatusCode();
        var bd = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/risks", new
        {
            title = "Subgrade material change", category = "Technical", probabilityPct = 25m,
            impactAmount = 4_000m, note = "geotech TBC", sortOrder = 2,
        })).Content.ReadFromJsonAsync<JsonElement>();

        var risks = bd.GetProperty("risks").EnumerateArray().ToList();
        Assert.Equal(2, risks.Count);
        Assert.Equal(500m,   risks[0].GetProperty("expectedValue").GetDecimal());
        Assert.Equal(1_000m, risks[1].GetProperty("expectedValue").GetDecimal());
        Assert.Equal(1_500m, bd.GetProperty("suggestedContingencyAmount").GetDecimal());
        Assert.Equal(10.00m, bd.GetProperty("suggestedContingencyPct").GetDecimal());
    }

    [Fact]
    public async Task Apply_as_contingency_writes_the_pct_to_the_contingency_markup_idempotently()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "RiskApplyTest");
        var sid = await Api.AddSectionAsync(admin, eid, $"S-{System.Guid.NewGuid():N}"[..6]);
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Work", unit = "ls", quantity = 1m, assemblyId = (int?)null,
            unitRate = 10_000m, sortOrder = 1,
        })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/risks", new
        {
            title = "Cost spike", category = "Cost", probabilityPct = 60m,
            impactAmount = 500m, note = (string?)null, sortOrder = 1,
        })).EnsureSuccessStatusCode();
        // EV = 300; pct = 300/10000 × 100 = 3.00%

        var applied = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/risks/apply-as-contingency", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var markups = applied.GetProperty("markups").EnumerateArray().ToList();
        var contingency = markups.First(m => m.GetProperty("type").GetString() == "Contingency");
        Assert.Equal(3.00m, contingency.GetProperty("percentage").GetDecimal());

        // Re-apply with no register change → STILL 3.00, not 6.00.
        var reapplied = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/risks/apply-as-contingency", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var contingency2 = reapplied.GetProperty("markups").EnumerateArray()
            .First(m => m.GetProperty("type").GetString() == "Contingency");
        Assert.Equal(3.00m, contingency2.GetProperty("percentage").GetDecimal());

        // And there's exactly one Contingency markup (no duplicates).
        var contingencyCount = reapplied.GetProperty("markups").EnumerateArray()
            .Count(m => m.GetProperty("type").GetString() == "Contingency");
        Assert.Equal(1, contingencyCount);
    }

    [Fact]
    public async Task Cashflow_distributes_bid_across_duration_months()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        // Set the project's DurationMonths so the S-curve has more than one bucket.
        var p = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}");
        var put = await admin.PutAsJsonAsync($"/api/projects/{pid}", new {
            name = p.GetProperty("name").GetString(),
            clientName = p.TryGetProperty("clientName", out var cn) ? cn.GetString() : null,
            location = p.TryGetProperty("location", out var loc) ? loc.GetString() : null,
            currency = p.GetProperty("currency").GetString(),
            durationMonths = 6, tenderDueAt = (string?)null, status = p.GetProperty("status").GetString(),
            projectTypeId = (int?)null,
        });
        put.EnsureSuccessStatusCode();

        var eid = await Api.NewEstimateAsync(admin, pid, "CashFlowTest");
        var sid = await Api.AddSectionAsync(admin, eid, $"S-{System.Guid.NewGuid():N}"[..6]);
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Job", unit = "ls", quantity = 1m, assemblyId = (int?)null,
            unitRate = 60_000m, sortOrder = 1,
        })).EnsureSuccessStatusCode();

        var bd = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        var cashflow = bd.GetProperty("cashFlow");
        Assert.Equal(6,        cashflow.GetProperty("durationMonths").GetInt32());
        Assert.Equal(60_000m,  cashflow.GetProperty("total").GetDecimal());

        var months = cashflow.GetProperty("monthly").EnumerateArray()
            .Select(m => m.GetProperty("spend").GetDecimal()).ToList();
        Assert.Equal(6, months.Count);
        // Sum exactly equals total (rounding residue absorbed in month 6).
        Assert.Equal(60_000m, months.Sum());
        // Cumulative at the final month equals the total.
        var cumLast = cashflow.GetProperty("monthly").EnumerateArray().Last()
            .GetProperty("cumulative").GetDecimal();
        Assert.Equal(60_000m, cumLast);
    }
}
