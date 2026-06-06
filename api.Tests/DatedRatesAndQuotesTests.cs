using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 18.3 coverage: dated resource rates (<see cref="ResourceRateHistory"/>),
/// supplier-quote register (<see cref="SupplierQuote"/>), and the engine's
/// pricing-date resolution. The three things estimators must trust:
///
///   1. Editing a resource rate auto-records the prior rate into history
///      (so "what was this on Jan 1?" is always answerable).
///   2. The rate engine, given a pricing date, returns the snapshot rate
///      effective at/before that date and falls back to the live rate when
///      no history applies.
///   3. The supplier-quote register exposes IsExpired so an estimator can see
///      at a glance which quotes are stale.
/// </summary>
[Collection("api")]
public class DatedRatesAndQuotesTests(ApiFixture fx)
{
    [Fact]
    public async Task Editing_a_labor_rate_auto_records_history_for_the_prior_rate()
    {
        var admin = await fx.AdminClientAsync();

        // Seed a fresh labor resource. PUT the rate twice — each change snapshots the
        // PRIOR rate (the rate as it ended, not the new one).
        var created = await (await admin.PostAsJsonAsync("/api/resources/labor", new
        {
            code = $"LAB-{System.Guid.NewGuid():N}"[..10], name = "Mason", unit = "hr",
            ratePerHour = 100m, isActive = true,
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        // 100 → 120
        (await admin.PutAsJsonAsync($"/api/resources/labor/{id}", new
        {
            code = "ignored", name = "Mason", unit = "hr", ratePerHour = 120m, isActive = true,
        })).EnsureSuccessStatusCode();
        // 120 → 150
        (await admin.PutAsJsonAsync($"/api/resources/labor/{id}", new
        {
            code = "ignored", name = "Mason", unit = "hr", ratePerHour = 150m, isActive = true,
        })).EnsureSuccessStatusCode();

        var hist = await admin.GetFromJsonAsync<JsonElement>($"/api/resources/labor/{id}/history");
        var rates = hist.EnumerateArray().Select(r => r.GetProperty("rate").GetDecimal()).ToList();
        // Two snapshots, each capturing the OUTGOING rate (the rate that just ended).
        // Ordering is newest-first, so 120 (prior to the 150 change) comes before 100.
        Assert.Equal(new[] { 120m, 100m }, rates);
    }

    [Fact]
    public async Task RateEngine_resolves_historical_rate_then_falls_back_to_live()
    {
        var admin = await fx.AdminClientAsync();
        // Seed a labor resource at the live rate.
        var created = await (await admin.PostAsJsonAsync("/api/resources/labor", new
        {
            code = $"LAB-{System.Guid.NewGuid():N}"[..10], name = "Carpenter", unit = "hr",
            ratePerHour = 200m, isActive = true,
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        // Manual back-date: in 2025, this trade cost 150/hr.
        (await admin.PostAsJsonAsync($"/api/resources/labor/{id}/history", new
        {
            effectiveFrom = "2025-01-01", rate = 150m, source = "supplier PO #PA-1"
        })).EnsureSuccessStatusCode();
        // And in 2024, it cost 120/hr.
        (await admin.PostAsJsonAsync($"/api/resources/labor/{id}/history", new
        {
            effectiveFrom = "2024-01-01", rate = 120m, source = "supplier PO #PA-0"
        })).EnsureSuccessStatusCode();

        // Drive the engine directly to prove the lookup semantics:
        //   • date in 2025  → most recent snapshot ≤ date is 2025 (150)
        //   • date in 2024  → most recent snapshot ≤ date is 2024 (120)
        //   • date in 2023  → no snapshot applies → fall back to live (200)
        //   • no date       → live (200)
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            var engine = new RateEngine(db);
            Assert.Equal(150m, await engine.ResolveRateAsync(ResourceType.Labor, id, liveRate: 200m, new DateOnly(2025, 6, 1)));
            Assert.Equal(120m, await engine.ResolveRateAsync(ResourceType.Labor, id, liveRate: 200m, new DateOnly(2024, 6, 1)));
            Assert.Equal(200m, await engine.ResolveRateAsync(ResourceType.Labor, id, liveRate: 200m, new DateOnly(2023, 6, 1)));
            Assert.Equal(200m, await engine.ResolveRateAsync(ResourceType.Labor, id, liveRate: 200m, pricingDate: null));
        });
    }

    [Fact]
    public async Task Setting_pricing_date_reprices_assembly_against_history()
    {
        var admin = await fx.AdminClientAsync();

        // Seed a labor resource currently at 200/hr, plus a 2024 snapshot at 120/hr.
        var labor = await (await admin.PostAsJsonAsync("/api/resources/labor", new
        {
            code = $"LAB-{System.Guid.NewGuid():N}"[..10], name = "Steel fitter", unit = "hr",
            ratePerHour = 200m, isActive = true,
        })).Content.ReadFromJsonAsync<JsonElement>();
        var laborId = labor.GetProperty("id").GetInt32();
        (await admin.PostAsJsonAsync($"/api/resources/labor/{laborId}/history", new
        {
            effectiveFrom = "2024-01-01", rate = 120m, source = "back-date"
        })).EnsureSuccessStatusCode();

        // Build an assembly: 1 hour of that labor per unit.
        var asm = await (await admin.PostAsJsonAsync("/api/assemblies", new
        {
            code = $"ASM-{System.Guid.NewGuid():N}"[..10], name = "Fab 1m", unit = "m", isActive = true,
        })).Content.ReadFromJsonAsync<JsonElement>();
        var asmId = asm.GetProperty("id").GetInt32();
        (await admin.PostAsJsonAsync($"/api/assemblies/{asmId}/components", new
        {
            resourceType = "Labor", resourceId = laborId, factor = 1m, sortOrder = 1,
        })).EnsureSuccessStatusCode();

        // Plug the assembly into a fresh BOQ item.
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "PricingDateTest");
        var sid = await Api.AddSectionAsync(admin, eid, $"S-{System.Guid.NewGuid():N}"[..6]);
        var addItem = await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Fab", unit = "m", quantity = 10m, assemblyId = asmId,
            unitRate = 0m, sortOrder = 1,
        });
        addItem.EnsureSuccessStatusCode();

        // Live recompute → unit rate = 200, line total = 2000.
        var live = await Api.PutMetaAsync(admin, eid, new { });
        live.EnsureSuccessStatusCode();
        var liveBd = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        var liveItem = liveBd.GetProperty("sections").EnumerateArray().First()
            .GetProperty("items").EnumerateArray().First();
        Assert.Equal(200m, liveItem.GetProperty("unitRate").GetDecimal());
        Assert.Equal(2_000m, liveItem.GetProperty("lineTotal").GetDecimal());

        // Set pricingDate to mid-2024 → resolves to 120 → line total = 1200.
        var dated = await Api.PutMetaAsync(admin, eid, new { pricingDate = "2024-06-01" });
        dated.EnsureSuccessStatusCode();
        var datedBd = await dated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2024-06-01", datedBd.GetProperty("pricingDate").GetString());
        var datedItem = datedBd.GetProperty("sections").EnumerateArray().First()
            .GetProperty("items").EnumerateArray().First();
        Assert.Equal(120m, datedItem.GetProperty("unitRate").GetDecimal());
        Assert.Equal(1_200m, datedItem.GetProperty("lineTotal").GetDecimal());

        // Clear pricingDate → back to the live rate.
        var cleared = await Api.PutMetaAsync(admin, eid, new { clearPricingDate = true });
        cleared.EnsureSuccessStatusCode();
        var clearedBd = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(clearedBd.GetProperty("pricingDate").ValueKind == JsonValueKind.Null);
        var clearedItem = clearedBd.GetProperty("sections").EnumerateArray().First()
            .GetProperty("items").EnumerateArray().First();
        Assert.Equal(200m, clearedItem.GetProperty("unitRate").GetDecimal());
    }

    [Fact]
    public async Task SupplierQuote_register_round_trip_and_expiry_flag()
    {
        var admin = await fx.AdminClientAsync();
        var today = DateOnly.FromDateTime(System.DateTime.UtcNow);

        // Create one quote valid for 30 days, and a second already expired.
        var live = await (await admin.PostAsJsonAsync("/api/quotes", new
        {
            resourceType = "material",  // tolerant of "material" or "materials"
            supplier = "Acme Steel", price = 350m, currency = "AED", unit = "t",
            quotedOn = today, validUntil = today.AddDays(30),
            note = "rebar, T16", attachmentUrl = (string?)null,
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(live.GetProperty("isExpired").GetBoolean());
        Assert.Equal("Material", live.GetProperty("resourceType").GetString());   // enum-cased on the way out

        var expired = await (await admin.PostAsJsonAsync("/api/quotes", new
        {
            resourceType = "materials", supplier = "Beta Steel", price = 360m, currency = "AED", unit = "t",
            quotedOn = today.AddDays(-60), validUntil = today.AddDays(-30),
            note = "stale", attachmentUrl = (string?)null,
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(expired.GetProperty("isExpired").GetBoolean());

        // List filters by type — both should appear when filtering by material.
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/quotes?type=material");
        var supplierNames = list.EnumerateArray().Select(q => q.GetProperty("supplier").GetString()).ToHashSet();
        Assert.Contains("Acme Steel", supplierNames);
        Assert.Contains("Beta Steel", supplierNames);

        // Validation: ValidUntil before QuotedOn → 400.
        var bad = await admin.PostAsJsonAsync("/api/quotes", new
        {
            resourceType = "material", supplier = "Bad", price = 1m, currency = "AED", unit = "t",
            quotedOn = today, validUntil = today.AddDays(-1),
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // DELETE round-trips cleanly.
        var liveId = live.GetProperty("id").GetInt32();
        var del = await admin.DeleteAsync($"/api/quotes/{liveId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    [Fact]
    public async Task Fx_refresh_touches_every_rate_and_writes_audit()
    {
        var admin = await fx.AdminClientAsync();

        // Seed a single currency rate so there's something to "review".
        (await admin.PutAsJsonAsync("/api/settings/currencies/USD", new { rateToBase = 3.6725m }))
            .EnsureSuccessStatusCode();

        var resp = await admin.PostAsync("/api/settings/currencies/refresh", content: null);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("reviewed").GetInt32() >= 1);

        // The refresh writes an "fx.refresh" audit row that an admin can see.
        var auditRaw = await admin.GetFromJsonAsync<JsonElement>("/api/audit?take=20");
        var items = auditRaw.GetProperty("items").EnumerateArray()
            .Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("fx.refresh", items);
    }
}
