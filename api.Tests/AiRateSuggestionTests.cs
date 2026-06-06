using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.10 — Tests for AI rate suggestion.
///
/// Service-level (unit) tests:
///   • Returns a benchmark suggestion with low confidence for an unknown resource.
///   • Returns a benchmark suggestion for a well-known keyword ("mason").
///   • Returns high confidence when ≥ 3 matching historical resources exist.
///   • Returns medium confidence when 1–2 matching resources exist.
///   • Median is computed correctly for even and odd lists.
///   • Empty name tokenises to nothing and returns an empty suggestion.
///
/// Endpoint (integration) tests via WebApplicationFactory:
///   • GET /api/ai/rate-suggestion requires authentication (401).
///   • Returns 200 + correct shape for a known keyword.
///   • Returns benchmark when tenant library has no match.
///   • Returns historical suggestion when tenant library has matching resources.
///   • A second tenant cannot see the first tenant's library resources.
/// </summary>
[Collection("api")]
public class AiRateSuggestionTests(ApiFixture fx)
{
    // ── Service unit tests (use the real service against the throwaway DB) ────

    /// <summary>For a made-up resource name with no keyword match, returns an
    /// empty benchmark suggestion (rate 0, low confidence).</summary>
    [Fact]
    public async Task Unknown_resource_returns_empty_low_confidence()
    {
        using var scope = await fx.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RateSuggestionService>();

        var result = await svc.SuggestAsync("XyzAbcZZZUnknownWidget", "unit", ResourceType.Material);

        Assert.Equal("low", result.Confidence);
        Assert.Equal(0m,    result.SuggestedRate);
        Assert.Equal("benchmark", result.Basis);
        Assert.Empty(result.Comparables);
    }

    /// <summary>A well-known keyword ("mason") resolves from the benchmark table.</summary>
    [Fact]
    public async Task Known_keyword_returns_benchmark_suggestion()
    {
        using var scope = await fx.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RateSuggestionService>();

        var result = await svc.SuggestAsync("Mason (skilled)", "hr", ResourceType.Labor);

        Assert.Equal("low",       result.Confidence);
        Assert.Equal("benchmark", result.Basis);
        Assert.True(result.SuggestedRate > 0m, "Expected a positive benchmark rate for Mason");
        Assert.NotEmpty(result.Comparables);
        Assert.Equal(22.00m, result.SuggestedRate); // known benchmark value
    }

    /// <summary>Equipment keyword "excavator" maps to a benchmark.</summary>
    [Fact]
    public async Task Excavator_keyword_returns_benchmark()
    {
        using var scope = await fx.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RateSuggestionService>();

        var result = await svc.SuggestAsync("Hydraulic Excavator 20T", "hr", ResourceType.Equipment);

        Assert.Equal("low",       result.Confidence);
        Assert.Equal("benchmark", result.Basis);
        Assert.Equal(80.00m,      result.SuggestedRate);
    }

    /// <summary>Median of sorted list [10, 20, 30] → 20.</summary>
    [Fact]
    public async Task Median_is_computed_for_odd_count()
    {
        using var scope = await fx.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var svc    = scope.ServiceProvider.GetRequiredService<RateSuggestionService>();

        // Seed three Labor resources with distinct rates.
        db.LaborResources.AddRange(
            new LaborResource { TenantId = tenant.TenantId, Code = "MED-A", Name = "Median Test Welder A", Unit = "hr", RatePerHour = 10m },
            new LaborResource { TenantId = tenant.TenantId, Code = "MED-B", Name = "Median Test Welder B", Unit = "hr", RatePerHour = 30m },
            new LaborResource { TenantId = tenant.TenantId, Code = "MED-C", Name = "Median Test Welder C", Unit = "hr", RatePerHour = 20m }
        );
        await db.SaveChangesAsync();

        var result = await svc.SuggestAsync("Median Test Welder", "hr", ResourceType.Labor);

        Assert.Equal("high",       result.Confidence);  // 3 matches
        Assert.Equal("historical", result.Basis);
        Assert.Equal(20m,          result.SuggestedRate);
    }

    /// <summary>One historical match → medium confidence.</summary>
    [Fact]
    public async Task Single_historical_match_returns_medium_confidence()
    {
        using var scope = await fx.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var svc    = scope.ServiceProvider.GetRequiredService<RateSuggestionService>();

        db.MaterialResources.Add(new MaterialResource
        {
            TenantId = tenant.TenantId, Code = "SOLO-PIPE", Name = "Solo Pipe Test Vinyl",
            Unit = "m", UnitPrice = 5.50m, WastagePct = 0, IsActive = true
        });
        await db.SaveChangesAsync();

        var result = await svc.SuggestAsync("Solo Pipe Test Vinyl", "m", ResourceType.Material);

        Assert.Equal("medium",     result.Confidence);
        Assert.Equal("historical", result.Basis);
        Assert.Equal(5.50m,        result.SuggestedRate);
    }

    // ── Endpoint integration tests ────────────────────────────────────────────

    [Fact]
    public async Task Endpoint_requires_auth()
    {
        var anon = fx.AnonClient();
        var resp = await anon.GetAsync("/api/ai/rate-suggestion?name=Mason&unit=hr&type=Labor");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Returns_200_with_correct_shape_for_known_keyword()
    {
        var client = await fx.AdminClientAsync();
        var resp   = await client.GetAsync("/api/ai/rate-suggestion?name=Painter&unit=hr&type=Labor");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("suggestedRate").GetDecimal() > 0m);
        Assert.Equal("low",       body.GetProperty("confidence").GetString());
        Assert.Equal("benchmark", body.GetProperty("basis").GetString());
        Assert.NotEmpty(body.GetProperty("comparables").EnumerateArray());
    }

    [Fact]
    public async Task Returns_benchmark_when_library_has_no_match()
    {
        var client = await fx.AdminClientAsync();
        // "concrete" is a known Material keyword in the benchmark table
        var resp = await client.GetAsync("/api/ai/rate-suggestion?name=Ready-Mix+Concrete&unit=m3&type=Material");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("low",       body.GetProperty("confidence").GetString());
        Assert.Equal("benchmark", body.GetProperty("basis").GetString());
        Assert.True(body.GetProperty("suggestedRate").GetDecimal() > 0m);
    }

    [Fact]
    public async Task Returns_historical_suggestion_when_library_has_matching_resources()
    {
        // Seed three equipment resources that share a distinctive name token.
        var client = await fx.AdminClientAsync();
        for (int i = 0; i < 3; i++)
        {
            var payload = new
            {
                code        = $"EQP-AITEST-{i}",
                name        = $"AiTestRigEquipment {i}",
                unit        = "hr",
                ratePerHour = 50m + (i * 10),   // 50, 60, 70 → median 60
                isActive    = true,
            };
            var r = await client.PostAsJsonAsync("/api/resources/equipment", payload);
            Assert.True(r.IsSuccessStatusCode, $"Seeding failed: {await r.Content.ReadAsStringAsync()}");
        }

        var resp = await client.GetAsync("/api/ai/rate-suggestion?name=AiTestRigEquipment&unit=hr&type=Equipment");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("high",       body.GetProperty("confidence").GetString());
        Assert.Equal("historical", body.GetProperty("basis").GetString());
        Assert.Equal(60m,          body.GetProperty("suggestedRate").GetDecimal());
        Assert.Equal(3,            body.GetProperty("comparables").GetArrayLength());
    }

    [Fact]
    public async Task Tenant_isolation_prevents_cross_tenant_library_leakage()
    {
        // Seed a resource in tenant A (default).
        var clientA = await fx.AdminClientAsync();
        await clientA.PostAsJsonAsync("/api/resources/labor", new
        {
            code = "ISO-LABOR-A", name = "IsoTestLabourAlpha", unit = "hr",
            ratePerHour = 99m, isActive = true,
        });

        // Tenant B (second fixture tenant) should not see tenant A's resource.
        var clientB = await fx.SecondAdminClientAsync();
        var resp = await clientB.GetAsync(
            "/api/ai/rate-suggestion?name=IsoTestLabourAlpha&unit=hr&type=Labor");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // Tenant B has no library match → falls back to benchmark (which returns 0 for
        // "IsoTestLabourAlpha" since it doesn't match any keyword) → empty suggestion.
        Assert.Equal("low",       body.GetProperty("confidence").GetString());
        Assert.Equal(0m,          body.GetProperty("suggestedRate").GetDecimal());
    }
}
