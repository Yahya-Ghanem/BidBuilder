using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 19.7 coverage. The FluentValidation endpoint filter runs BEFORE the handler
/// and short-circuits to RFC-7807 ProblemDetails with a per-field <c>errors</c>
/// dictionary. We prove:
///
///   • Multiple field violations are reported together (not one-at-a-time as
///     the legacy if-statement guards used to do) — the frontend gets the full
///     diagnosis in a single response.
///   • The response shape is the standard ASP.NET ValidationProblemDetails the
///     generated TS client + every off-the-shelf form library already understand.
///   • A valid payload still passes through to the handler unchanged.
/// </summary>
[Collection("api")]
public class ValidationTests(ApiFixture fx)
{
    [Fact]
    public async Task Labor_post_rejects_negative_rate_with_structured_problem_details()
    {
        var admin = await fx.AdminClientAsync();
        var bad = await admin.PostAsJsonAsync("/api/resources/labor", new
        {
            code = "", name = "", unit = "hr", ratePerHour = -50m, isActive = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // RFC-7807 ValidationProblemDetails shape.
        var body = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        var errors = body.GetProperty("errors");

        // Reports ALL three issues at once — code empty, name empty, rate negative.
        Assert.True(errors.TryGetProperty("Code", out _));
        Assert.True(errors.TryGetProperty("Name", out _));
        Assert.True(errors.TryGetProperty("RatePerHour", out var rate));
        Assert.Contains("negative", rate.EnumerateArray().First().GetString()!.ToLowerInvariant());
    }

    [Fact]
    public async Task Material_post_rejects_out_of_range_wastage_and_negative_price()
    {
        var admin = await fx.AdminClientAsync();
        var bad = await admin.PostAsJsonAsync("/api/resources/materials", new
        {
            code = $"MAT-FV-{System.Guid.NewGuid():N}"[..14],
            name = "Bad Mat", unit = "kg", unitPrice = -1m, wastagePct = 150m,
            supplier = (string?)null, isActive = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadFromJsonAsync<JsonElement>();
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("UnitPrice", out _));
        Assert.True(errors.TryGetProperty("WastagePct", out _));
    }

    [Fact]
    public async Task Quote_post_rejects_validUntil_before_quotedOn_via_validator()
    {
        var admin = await fx.AdminClientAsync();
        var today = DateOnly.FromDateTime(System.DateTime.UtcNow);
        var bad = await admin.PostAsJsonAsync("/api/quotes", new
        {
            resourceType = "material", supplier = "ValidatorCo",
            price = 100m, currency = "AED", unit = "t",
            quotedOn = today, validUntil = today.AddDays(-5),
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadFromJsonAsync<JsonElement>();
        // The OverridePropertyName surfaces the date issue under "ValidUntil".
        Assert.True(body.GetProperty("errors").TryGetProperty("ValidUntil", out _));
    }

    [Fact]
    public async Task BidOutcome_patch_rejects_negative_money_via_validator()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var bad = await admin.PatchAsJsonAsync($"/api/projects/{pid}/bid-outcome", new
        {
            submittedBidValue = -1m,
            awardedValue      = -2m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadFromJsonAsync<JsonElement>();
        var errors = body.GetProperty("errors");
        // BOTH fields surfaced — not a one-at-a-time bail like the legacy if-checks.
        Assert.True(errors.TryGetProperty("SubmittedBidValue", out _));
        Assert.True(errors.TryGetProperty("AwardedValue", out _));
    }

    [Fact]
    public async Task Valid_payload_passes_validator_and_reaches_handler()
    {
        var admin = await fx.AdminClientAsync();
        var ok = await admin.PostAsJsonAsync("/api/resources/labor", new
        {
            code = $"LAB-FV-{System.Guid.NewGuid():N}"[..14],
            name = "Valid", unit = "hr", ratePerHour = 75m, isActive = true,
        });
        Assert.True(ok.IsSuccessStatusCode);
    }
}
