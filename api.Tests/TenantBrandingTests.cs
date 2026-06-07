using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 24.5 — Tenant branding text (header / footer / signature) on TenantSettings,
/// rendered onto the bid-letter PDF. Newline-preserved multi-line plain text;
/// no HTML / scripts (the PDF renderer treats input as text, not markup, which
/// is also the XSS defense).
///
/// Coverage:
///   • Round-trip: PUT then GET round-trips all three fields verbatim.
///   • Partial update: passing one field leaves the others alone (null = skip).
///   • Clear: empty string clears a previously-set value (round-trips as null).
///   • Oversize (> 2 KB): rejected with 400.
///   • Non-admin: 403.
/// </summary>
[Collection("api")]
public class TenantBrandingTests(ApiFixture fx)
{
    private const string SampleHeader = "ACME Construction LLC\nLicensed General Contractor — Lic. #AB-12345";
    private const string SampleFooter = "© ACME Construction LLC · Confidential";
    private const string SampleSignature = "Yours faithfully,\nACME Construction LLC\nTender Office";

    private static async Task<JsonElement> PutSettingsAsync(HttpClient admin, object body, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var resp = await admin.PutAsJsonAsync("/api/settings/", body);
        Assert.Equal(expect, resp.StatusCode);
        return expect == HttpStatusCode.OK
            ? await resp.Content.ReadFromJsonAsync<JsonElement>()
            : default;
    }

    [Fact]
    public async Task Round_trip_preserves_all_three_brand_fields_verbatim()
    {
        var admin = await fx.AdminClientAsync();

        var put = await PutSettingsAsync(admin, new
        {
            baseCurrency = "AED", timezone = "UTC",
            defaultOverheadPct = 0m, defaultProfitPct = 0m, defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            brandHeaderText = SampleHeader,
            brandFooterText = SampleFooter,
            brandSignatureText = SampleSignature,
        });

        Assert.Equal(SampleHeader, put.GetProperty("brandHeaderText").GetString());
        Assert.Equal(SampleFooter, put.GetProperty("brandFooterText").GetString());
        Assert.Equal(SampleSignature, put.GetProperty("brandSignatureText").GetString());

        // And the GET sees the same.
        var get = await admin.GetFromJsonAsync<JsonElement>("/api/settings/");
        Assert.Equal(SampleHeader, get.GetProperty("brandHeaderText").GetString());
        Assert.Equal(SampleFooter, get.GetProperty("brandFooterText").GetString());
        Assert.Equal(SampleSignature, get.GetProperty("brandSignatureText").GetString());
    }

    [Fact]
    public async Task Null_brand_field_leaves_existing_value_alone()
    {
        var admin = await fx.AdminClientAsync();

        // Seed all three.
        await PutSettingsAsync(admin, new
        {
            baseCurrency = "AED", timezone = "UTC",
            defaultOverheadPct = 0m, defaultProfitPct = 0m, defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            brandHeaderText = SampleHeader,
            brandFooterText = SampleFooter,
            brandSignatureText = SampleSignature,
        });

        // Now update ONLY the footer; the other two should stay.
        var put = await PutSettingsAsync(admin, new
        {
            baseCurrency = "AED", timezone = "UTC",
            defaultOverheadPct = 0m, defaultProfitPct = 0m, defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            brandFooterText = "© 2026 ACME — Updated",
            // brandHeaderText and brandSignatureText omitted → null → leave alone
        });

        Assert.Equal(SampleHeader, put.GetProperty("brandHeaderText").GetString());
        Assert.Equal("© 2026 ACME — Updated", put.GetProperty("brandFooterText").GetString());
        Assert.Equal(SampleSignature, put.GetProperty("brandSignatureText").GetString());
    }

    [Fact]
    public async Task Empty_string_clears_a_set_field()
    {
        var admin = await fx.AdminClientAsync();

        await PutSettingsAsync(admin, new
        {
            baseCurrency = "AED", timezone = "UTC",
            defaultOverheadPct = 0m, defaultProfitPct = 0m, defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            brandHeaderText = SampleHeader,
        });

        // Clear with empty string — round-trips as null (NormalizeBrand returns null on whitespace).
        var put = await PutSettingsAsync(admin, new
        {
            baseCurrency = "AED", timezone = "UTC",
            defaultOverheadPct = 0m, defaultProfitPct = 0m, defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            brandHeaderText = "",
        });

        Assert.Equal(JsonValueKind.Null, put.GetProperty("brandHeaderText").ValueKind);
    }

    [Fact]
    public async Task Oversize_brand_field_is_rejected_with_400()
    {
        var admin = await fx.AdminClientAsync();
        var huge = new string('x', 2049);   // > 2 KB cap
        await PutSettingsAsync(admin, new
        {
            baseCurrency = "AED", timezone = "UTC",
            defaultOverheadPct = 0m, defaultProfitPct = 0m, defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            brandHeaderText = huge,
        }, expect: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Whitespace_only_brand_field_round_trips_as_null()
    {
        // NormalizeBrand returns null on whitespace, so "   \n  " clears just like "".
        // Guards against a tenant accidentally setting an invisible field that would
        // still pass the IsNullOrWhiteSpace check in the renderer but waste a header line.
        var admin = await fx.AdminClientAsync();
        await PutSettingsAsync(admin, new
        {
            baseCurrency = "AED", timezone = "UTC",
            defaultOverheadPct = 0m, defaultProfitPct = 0m, defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            brandSignatureText = SampleSignature,
        });
        var put = await PutSettingsAsync(admin, new
        {
            baseCurrency = "AED", timezone = "UTC",
            defaultOverheadPct = 0m, defaultProfitPct = 0m, defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            brandSignatureText = "   \n   ",
        });
        Assert.Equal(JsonValueKind.Null, put.GetProperty("brandSignatureText").ValueKind);
    }
}
