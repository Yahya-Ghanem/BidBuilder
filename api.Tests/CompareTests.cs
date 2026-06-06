using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.5 — Tests for the bid comparison endpoint
/// (<c>GET /api/estimates/compare?ids=…</c>).
///
/// Coverage:
///   • Two revisions return one column each, sections aligned by code, and a
///     section present in only one estimate shows null in the other column.
///   • The id-count guard: fewer than two and more than four → 400.
///   • An estimate the caller can't see (cross-tenant) → 404 (existence hidden).
///   • Estimates in different currencies set MixedCurrency = true.
/// </summary>
[Collection("api")]
public class CompareTests(ApiFixture fx)
{
    static Task<HttpResponseMessage> AddItemAsync(HttpClient c, int eid, int sid, decimal qty, decimal rate) =>
        c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items",
            new { description = "line", unit = "m", quantity = qty, unitRate = rate, sortOrder = 0 });

    static decimal? Cell(JsonElement totals, int i) =>
        totals[i].ValueKind == JsonValueKind.Null ? null : totals[i].GetDecimal();

    [Fact]
    public async Task Compares_two_revisions_aligning_sections_by_code()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);

        // Estimate A: section S1 = 2×100 = 200, section S2 = 1×50 = 50.
        var a = await Api.NewEstimateAsync(admin, pid, "compare-A");
        var aS1 = await Api.AddSectionAsync(admin, a, "S1");
        (await AddItemAsync(admin, a, aS1, 2, 100)).EnsureSuccessStatusCode();
        var aS2 = await Api.AddSectionAsync(admin, a, "S2");
        (await AddItemAsync(admin, a, aS2, 1, 50)).EnsureSuccessStatusCode();

        // Estimate B: section S1 = 3×100 = 300 (no S2).
        var b = await Api.NewEstimateAsync(admin, pid, "compare-B");
        var bS1 = await Api.AddSectionAsync(admin, b, "S1");
        (await AddItemAsync(admin, b, bS1, 3, 100)).EnsureSuccessStatusCode();

        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/compare?ids={a}&ids={b}");

        Assert.False(view.GetProperty("mixedCurrency").GetBoolean());
        var cols = view.GetProperty("columns");
        Assert.Equal(2, cols.GetArrayLength());
        Assert.Equal(a, cols[0].GetProperty("estimateId").GetInt32());
        Assert.Equal(b, cols[1].GetProperty("estimateId").GetInt32());

        var sections = view.GetProperty("sections").EnumerateArray().ToList();
        var s1 = sections.First(s => s.GetProperty("code").GetString() == "S1").GetProperty("totals");
        var s2 = sections.First(s => s.GetProperty("code").GetString() == "S2").GetProperty("totals");
        Assert.Equal(200m, Cell(s1, 0));
        Assert.Equal(300m, Cell(s1, 1));
        Assert.Equal(50m, Cell(s2, 0));
        Assert.Null(Cell(s2, 1));   // S2 absent in estimate B
    }

    [Fact]
    public async Task Rejects_fewer_than_two_ids()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var a = await Api.NewEstimateAsync(admin, pid, "compare-min");
        var r = await admin.GetAsync($"/api/estimates/compare?ids={a}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Rejects_more_than_four_ids()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var ids = new List<int>();
        for (int i = 0; i < 5; i++) ids.Add(await Api.NewEstimateAsync(admin, pid, $"compare-max-{i}"));
        var qs = string.Join("&", ids.Select(id => $"ids={id}"));
        var r = await admin.GetAsync($"/api/estimates/compare?{qs}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Hides_estimates_the_caller_cannot_access_with_404()
    {
        // Two estimates owned by the default tenant.
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var a = await Api.NewEstimateAsync(admin, pid, "compare-iso-a");
        var b = await Api.NewEstimateAsync(admin, pid, "compare-iso-b");

        // A different tenant's admin can't see them — the global query filter makes
        // them invisible, so the per-estimate access guard returns 404.
        var other = await fx.SecondTenantAdminClientAsync();
        var r = await other.GetAsync($"/api/estimates/compare?ids={a}&ids={b}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Flags_mixed_currency()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var a = await Api.NewEstimateAsync(admin, pid, "compare-cur-a");
        var b = await Api.NewEstimateAsync(admin, pid, "compare-cur-b");

        // Flip estimate B to a different currency directly (no meta path changes
        // an estimate's native currency — it's inherited from the project on create).
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            var est = await db.Estimates.FirstAsync(e => e.Id == b);
            est.Currency = est.Currency == "USD" ? "EUR" : "USD";
            await db.SaveChangesAsync();
        });

        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/compare?ids={a}&ids={b}");
        Assert.True(view.GetProperty("mixedCurrency").GetBoolean());
    }
}
