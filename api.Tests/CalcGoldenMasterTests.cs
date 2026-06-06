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
/// Golden-master + reconcile coverage for the estimating engine. A representative
/// estimate (assembly-priced, ad-hoc and component-build-up items across two sections;
/// fixed + time-related preliminaries; all four compounding markups; a secondary
/// presentation currency) is recomputed and its full set of figures asserted against a
/// hand-verified snapshot. If a rounding rule or the markup order ever changes, these
/// pinned numbers break — exactly the early-warning a money engine needs. The reconcile
/// tests prove the drift safety-net detects and repairs a corrupted cache.
/// </summary>
[Collection("api")]
public class CalcGoldenMasterTests(ApiFixture fx)
{
    // Hand-verified expected snapshot (see arithmetic in comments below the seed):
    private const decimal ExpectedDirect   = 39_455.00m;
    private const decimal ExpectedIndirect = 17_000.00m;
    private const decimal ExpectedMarkup   = 17_398.44m;
    private const decimal ExpectedBid      = 73_853.44m;

    [Fact]
    public async Task Golden_master_full_buildup_matches_snapshot()
    {
        var id = await SeedGoldenEstimateAsync();

        EstimateBreakdown bd = null!;
        bool reReconcileClean = false;
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            bd = (await new EstimateCalculator(db).RecomputeAsync(id))!;
        });
        // A recompute must be a FIXED POINT: reconciling immediately after reports no drift.
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            var r = await new EstimateCalculator(db).ReconcileAsync(id, commit: false);
            reReconcileClean = r is { Drifted: false };
        });

        // ── Totals ───────────────────────────────────────────────────────────
        Assert.Equal(ExpectedDirect,   bd.DirectCost);
        Assert.Equal(ExpectedIndirect, bd.IndirectCost);
        Assert.Equal(ExpectedMarkup,   bd.MarkupCost);
        Assert.Equal(ExpectedBid,      bd.BidPrice);

        // ── Section roll-ups ──────────────────────────────────────────────────
        Assert.Equal(39_125.00m, bd.Sections.Single(s => s.Code == "A").SectionTotal);  // 250×124.50 + 100×80
        Assert.Equal(330.00m,    bd.Sections.Single(s => s.Code == "B").SectionTotal);  // 2 × (100+50 +10%) = 2×165

        // ── Component build-up item: unit rate 165, line total 330 ────────────
        var buildUp = bd.Sections.Single(s => s.Code == "B").Items.Single();
        Assert.Equal(165.00m, buildUp.UnitRate);
        Assert.Equal(330.00m, buildUp.LineTotal);

        // ── Gross margin on price = MarkupCost / BidPrice = 17,398.44 / 73,853.44 ──
        Assert.Equal(23.56m, bd.MarginOnPricePct);

        // ── Markups compound in order: 8% → 12% → 5% → 3% on base 56,455 ──────
        Assert.Equal(
            new[] { 4_516.40m, 7_316.57m, 3_414.40m, 2_151.07m },
            bd.Markups.OrderBy(m => m.ApplyOrder).Select(m => m.ComputedAmount).ToArray());

        // ── Presentation currency: live (Draft) cross-rate 0.5 → converted bid ─
        Assert.NotNull(bd.Fx);
        Assert.False(bd.Fx!.Frozen);
        Assert.Equal(0.5m, bd.Fx.Rate);
        Assert.Equal(36_926.72m, bd.Fx.ConvertedBidPrice);   // 73,853.44 × 0.5

        // ── No tax configured: tax amount is zero, inclusive total equals the bid. ──
        Assert.Equal(0m, bd.TaxAmount);
        Assert.Equal(bd.BidPrice, bd.BidPriceInclTax);

        Assert.True(reReconcileClean, "recompute should be a fixed point (no drift on immediate reconcile)");
    }

    [Fact]
    public async Task Tax_is_applied_after_markups_and_reported_separately()
    {
        var id = await SeedGoldenEstimateAsync();
        var admin = await fx.AdminClientAsync();

        // Setting a 5% VAT recomputes; tax is taken on the finished bid, never compounded.
        var resp = await admin.PutAsJsonAsync($"/api/estimates/{id}", new { taxRatePct = 5m });
        resp.EnsureSuccessStatusCode();
        var bd = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(ExpectedBid, bd.GetProperty("bidPrice").GetDecimal());          // unchanged by tax
        Assert.Equal(5m,          bd.GetProperty("taxRatePct").GetDecimal());
        Assert.Equal(3_692.67m,   bd.GetProperty("taxAmount").GetDecimal());         // 73,853.44 × 5%
        Assert.Equal(77_546.11m,  bd.GetProperty("bidPriceInclTax").GetDecimal());   // bid + VAT
    }

    [Fact]
    public async Task Reconcile_detects_then_repairs_a_corrupted_cache_over_http()
    {
        var id = await SeedGoldenEstimateAsync();
        var admin = await fx.AdminClientAsync();

        // Freshly seeded: cached totals are still zero (never recomputed) → drift.
        var first = await Reconcile(admin, id, commit: false);
        Assert.True(first.GetProperty("drifted").GetBoolean());
        Assert.Equal(0m, first.GetProperty("before").GetProperty("bidPrice").GetDecimal());
        Assert.Equal(ExpectedBid, first.GetProperty("after").GetProperty("bidPrice").GetDecimal());

        // commit=true persists the corrected totals…
        var committed = await Reconcile(admin, id, commit: true);
        Assert.True(committed.GetProperty("drifted").GetBoolean());

        // …so a subsequent reconcile sees a clean, drift-free cache.
        var clean = await Reconcile(admin, id, commit: false);
        Assert.False(clean.GetProperty("drifted").GetBoolean());
        Assert.Equal(ExpectedBid, clean.GetProperty("before").GetProperty("bidPrice").GetDecimal());
    }

    [Fact]
    public async Task Line_kinds_bucket_correctly_provisional_not_marked_up_alternate_excluded()
    {
        int id = 0;
        EstimateBreakdown bd = null!;
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            var project = new Project { Code = $"GMK-{System.Guid.NewGuid():N}"[..12], Name = "Kinds", Currency = "AED", DurationMonths = 1, Status = ProjectStatus.Draft };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var est = new Estimate { ProjectId = project.Id, Revision = 1, Title = "K", Status = EstimateStatus.Draft, Currency = "AED" };
            db.Estimates.Add(est);
            await db.SaveChangesAsync();
            var sec = new BoqSection { EstimateId = est.Id, Code = "A", Title = "A", SortOrder = 1 };
            db.BoqSections.Add(sec);
            await db.SaveChangesAsync();
            db.BoqItems.Add(new BoqItem { SectionId = sec.Id, Description = "Normal",      Unit = "no", Quantity = 1m, UnitRate = 1_000m, SortOrder = 1, Kind = BoqItemKind.Normal });
            db.BoqItems.Add(new BoqItem { SectionId = sec.Id, Description = "Provisional",  Unit = "no", Quantity = 1m, UnitRate = 500m,   SortOrder = 2, Kind = BoqItemKind.ProvisionalSum });
            db.BoqItems.Add(new BoqItem { SectionId = sec.Id, Description = "Alternate",    Unit = "no", Quantity = 1m, UnitRate = 999m,   SortOrder = 3, Kind = BoqItemKind.Alternate });
            db.Markups.Add(new Markup { EstimateId = est.Id, Type = MarkupType.Profit, Percentage = 10m, ApplyOrder = 1 });
            await db.SaveChangesAsync();
            id = est.Id;
        });
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            bd = (await new EstimateCalculator(db).RecomputeAsync(id))!;
        });

        Assert.Equal(1_500.00m, bd.DirectCost);        // Normal 1000 + Provisional 500 (alternate excluded)
        Assert.Equal(100.00m,   bd.MarkupCost);        // 10% of the markupable 1000 ONLY — not 1500
        Assert.Equal(1_600.00m, bd.BidPrice);          // 1500 + 100
        Assert.Equal(999.00m,   bd.AlternatesTotal);   // carried separately, out of the bid
        Assert.Equal(1_500.00m, bd.Sections.Single().SectionTotal);   // bid lines only
    }

    private static async Task<JsonElement> Reconcile(HttpClient c, int id, bool commit)
    {
        var resp = await c.PostAsync($"/api/estimates/{id}/reconcile?commit={(commit ? "true" : "false")}", null);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Seed a representative estimate directly in the DB (no recompute, so the cached
    /// totals start at zero). Returns the estimate id.
    ///
    /// Arithmetic of the expected snapshot:
    ///   Direct  = Section A (250×124.50 = 31,125  +  100×80 = 8,000)  +  Section B (2×165 = 330) = 39,455
    ///   Indirect= Fixed 5,000  +  TimeRelated 2,000/mo × 6 = 12,000                                = 17,000
    ///   Markups on 56,455: OH 8% (4,516.40) → Profit 12% (7,316.57) → Cont 5% (3,414.40) → Esc 3% (2,151.07)
    ///   Markup total = 17,398.44 ; Bid = 56,455 + 17,398.44 = 73,853.44
    /// </summary>
    private async Task<int> SeedGoldenEstimateAsync()
    {
        int estimateId = 0;
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            // Base currency = whatever the tenant settings say (CrossRate uses the same default).
            var baseC = (await db.TenantSettings.Select(s => s.BaseCurrency).FirstOrDefaultAsync()) ?? "AED";
            var secondary = string.Equals(baseC, "USD", System.StringComparison.OrdinalIgnoreCase) ? "EUR" : "USD";

            // 1 unit of `secondary` = 2 base units → cross-rate(base→secondary) = 1/2 = 0.5 exactly.
            var rate = await db.CurrencyRates.FirstOrDefaultAsync(r => r.Code == secondary);
            if (rate is null) db.CurrencyRates.Add(new CurrencyRate { Code = secondary, RateToBase = 2.0m });
            else rate.RateToBase = 2.0m;

            // Two distinct Amount-kind types (values 100 + 50) and one Percent-kind type (10%) → rate 165.
            var amountTypes = await db.CostComponentTypes
                .Where(t => t.CalcKind == CostCalcKind.Amount).OrderBy(t => t.SortOrder).Take(2).ToListAsync();
            var percentType = await db.CostComponentTypes
                .Where(t => t.CalcKind == CostCalcKind.Percent).OrderBy(t => t.SortOrder).FirstAsync();

            var asm = new Assembly { Code = $"GM-{System.Guid.NewGuid():N}"[..14], Name = "GM Assembly", Unit = "m3", ComputedRate = 124.50m, IsActive = true };
            db.Assemblies.Add(asm);

            var project = new Project { Code = $"GMP-{System.Guid.NewGuid():N}"[..12], Name = "Golden Master", Currency = baseC, DurationMonths = 6, Status = ProjectStatus.Draft };
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            var est = new Estimate { ProjectId = project.Id, Revision = 1, Title = "GM", Status = EstimateStatus.Draft, Currency = baseC, SecondaryCurrency = secondary };
            db.Estimates.Add(est);
            await db.SaveChangesAsync();

            var secA = new BoqSection { EstimateId = est.Id, Code = "A", Title = "Section A", SortOrder = 1 };
            var secB = new BoqSection { EstimateId = est.Id, Code = "B", Title = "Section B", SortOrder = 2 };
            db.BoqSections.AddRange(secA, secB);
            await db.SaveChangesAsync();

            db.BoqItems.Add(new BoqItem { SectionId = secA.Id, Description = "Assembly item", Unit = "m3", Quantity = 250m, AssemblyId = asm.Id, UnitRate = 0m, SortOrder = 1 });
            db.BoqItems.Add(new BoqItem { SectionId = secA.Id, Description = "Ad-hoc item", Unit = "m2", Quantity = 100m, UnitRate = 80m, SortOrder = 2 });
            var compItem = new BoqItem { SectionId = secB.Id, Description = "Build-up item", Unit = "no", Quantity = 2m, UnitRate = 0m, SortOrder = 1 };
            compItem.CostComponents.Add(new ItemCostComponent { CostComponentTypeId = amountTypes[0].Id, Value = 100m });
            compItem.CostComponents.Add(new ItemCostComponent { CostComponentTypeId = amountTypes[1].Id, Value = 50m });
            compItem.CostComponents.Add(new ItemCostComponent { CostComponentTypeId = percentType.Id, Value = 10m });
            db.BoqItems.Add(compItem);

            db.Preliminaries.Add(new Preliminary { EstimateId = est.Id, Description = "Mobilization", Kind = PreliminaryKind.Fixed, Amount = 5_000m, SortOrder = 1 });
            db.Preliminaries.Add(new Preliminary { EstimateId = est.Id, Description = "Site staff", Kind = PreliminaryKind.TimeRelated, Amount = 2_000m, SortOrder = 2 });

            db.Markups.Add(new Markup { EstimateId = est.Id, Type = MarkupType.Overhead,    Percentage = 8m,  ApplyOrder = 1 });
            db.Markups.Add(new Markup { EstimateId = est.Id, Type = MarkupType.Profit,      Percentage = 12m, ApplyOrder = 2 });
            db.Markups.Add(new Markup { EstimateId = est.Id, Type = MarkupType.Contingency, Percentage = 5m,  ApplyOrder = 3 });
            db.Markups.Add(new Markup { EstimateId = est.Id, Type = MarkupType.Escalation,  Percentage = 3m,  ApplyOrder = 4 });
            await db.SaveChangesAsync();

            estimateId = est.Id;
        });
        return estimateId;
    }
}
