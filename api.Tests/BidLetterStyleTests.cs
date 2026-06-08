using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 28.4 coverage: bid-letter styles. Acceptance bar: "All three styles render with
/// the same project data; tenant default preserved; existing branding text (24.5)
/// honoured in all three."
///
/// Coverage:
///   1. All three styles return a valid PDF (%PDF magic + non-trivial size) for the
///      seeded project. None of them throws even when the estimate has no items or
///      no tax — the renderer must tolerate sparse data.
///   2. Without ?style=, the endpoint falls back to TenantSettings.DefaultBidLetterStyle.
///      Setting the default to "Concise" then downloading without ?style= must render
///      the concise body (proxy: byte length differs from the default Formal output for
///      the same project — same data through two layouts → different page weight).
///   3. ?style= overrides the tenant default for that one request.
///   4. ?style=Garbage returns 400 with a clear message — silently falling back to
///      Formal would hide UX bugs.
///   5. PUT /api/settings with an unknown DefaultBidLetterStyle is rejected 400.
///   6. Branding (24.5) survives across all three styles: when BrandHeaderText is set,
///      every rendered style contains its bytes (proxy: PDF text is embedded as glyphs,
///      so we assert by length growth — a populated header makes a longer document).
/// </summary>
[Collection("api")]
public class BidLetterStyleTests(ApiFixture fx)
{
    private static bool LooksLikePdf(byte[] b) =>
        b.Length > 4 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F';

    private static async Task<byte[]> Letter(HttpClient c, int eid, string? style)
    {
        var url = $"/api/estimates/{eid}/bid-letter.pdf"
                  + (style is null ? "" : $"?style={style}");
        var r = await c.GetAsync(url);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsByteArrayAsync();
    }

    private static async Task SetSettings(HttpClient c, object body)
    {
        // The settings PUT requires every percent field to be present (non-null),
        // so we read the current row and only flip the field we care about.
        var current = await c.GetFromJsonAsync<JsonElement>("/api/settings");
        var merged = new Dictionary<string, object?>();
        foreach (var p in current.EnumerateObject()) merged[p.Name] = p.Value.ValueKind switch
        {
            JsonValueKind.String => p.Value.GetString(),
            JsonValueKind.Number => (object?)p.Value.GetDecimal(),
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.Null   => null,
            _                    => null,
        };
        foreach (var (k, v) in (IEnumerable<KeyValuePair<string, object?>>)body.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(body)))
            merged[k] = v;
        var resp = await c.PutAsJsonAsync("/api/settings", merged);
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task All_three_styles_render_a_valid_pdf_for_the_same_estimate()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        var formal        = await Letter(admin, eid, "Formal");
        var concise       = await Letter(admin, eid, "Concise");
        var international = await Letter(admin, eid, "International");

        Assert.True(LooksLikePdf(formal),        "Formal: not a PDF");
        Assert.True(LooksLikePdf(concise),       "Concise: not a PDF");
        Assert.True(LooksLikePdf(international), "International: not a PDF");

        // Same data → different layouts → different byte lengths. If two of them
        // returned bytes-equal output, the style-dispatch is silently broken.
        Assert.NotEqual(formal.Length, concise.Length);
        Assert.NotEqual(formal.Length, international.Length);
        Assert.NotEqual(concise.Length, international.Length);
    }

    [Fact]
    public async Task Missing_style_falls_back_to_tenant_default()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        // Pin the tenant default to Formal first so the baseline is known.
        await SetSettings(admin, new { DefaultBidLetterStyle = "Formal" });
        var noStyleFormal = await Letter(admin, eid, null);
        var explicitFormal = await Letter(admin, eid, "Formal");
        Assert.Equal(explicitFormal.Length, noStyleFormal.Length);

        // Flip default to Concise; the no-style request must now render the
        // concise body (proxy: byte length matches the explicit Concise output).
        await SetSettings(admin, new { DefaultBidLetterStyle = "Concise" });
        var noStyleConcise = await Letter(admin, eid, null);
        var explicitConcise = await Letter(admin, eid, "Concise");
        Assert.Equal(explicitConcise.Length, noStyleConcise.Length);
        Assert.NotEqual(explicitFormal.Length, noStyleConcise.Length);

        // Restore the default so we don't leak state into other tests in the
        // shared fixture collection.
        await SetSettings(admin, new { DefaultBidLetterStyle = "Formal" });
    }

    [Fact]
    public async Task Explicit_style_overrides_tenant_default()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        await SetSettings(admin, new { DefaultBidLetterStyle = "Formal" });
        var formalDefault = await Letter(admin, eid, null);
        // ?style=International must win over the Formal tenant default.
        var requested = await Letter(admin, eid, "International");
        Assert.NotEqual(formalDefault.Length, requested.Length);
        var explicitIntl = await Letter(admin, eid, "International");
        Assert.Equal(explicitIntl.Length, requested.Length);
    }

    [Fact]
    public async Task Unknown_style_query_is_rejected_400()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        var resp = await admin.GetAsync($"/api/estimates/{eid}/bid-letter.pdf?style=Garbage");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Garbage", body.GetProperty("error").GetString()!);
        Assert.Contains("Formal",  body.GetProperty("error").GetString()!); // mentions allowed set
    }

    [Fact]
    public async Task Settings_put_rejects_unknown_default_style()
    {
        var admin = await fx.AdminClientAsync();
        var current = await admin.GetFromJsonAsync<JsonElement>("/api/settings");
        var merged = new Dictionary<string, object?>();
        foreach (var p in current.EnumerateObject()) merged[p.Name] = p.Value.ValueKind switch
        {
            JsonValueKind.String => p.Value.GetString(),
            JsonValueKind.Number => (object?)p.Value.GetDecimal(),
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            _                    => null,
        };
        merged["DefaultBidLetterStyle"] = "Whimsical";
        var resp = await admin.PutAsJsonAsync("/api/settings", merged);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Branding_header_survives_all_three_styles()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        // Baseline lengths with NO branding header text.
        await SetSettings(admin, new { BrandHeaderText = "" });
        var baseFormal        = (await Letter(admin, eid, "Formal")).Length;
        var baseConcise       = (await Letter(admin, eid, "Concise")).Length;
        var baseInternational = (await Letter(admin, eid, "International")).Length;

        // Now set a substantial header and re-render — every style's document
        // must grow (the header is rendered via shared LetterHeader()).
        await SetSettings(admin, new { BrandHeaderText = "ISO 9001 certified · Member of UAE Contractors Association · Established 1998" });
        var withFormal        = (await Letter(admin, eid, "Formal")).Length;
        var withConcise       = (await Letter(admin, eid, "Concise")).Length;
        var withInternational = (await Letter(admin, eid, "International")).Length;

        Assert.True(withFormal        > baseFormal,        $"Formal: header text not honoured ({baseFormal} → {withFormal})");
        Assert.True(withConcise       > baseConcise,       $"Concise: header text not honoured ({baseConcise} → {withConcise})");
        Assert.True(withInternational > baseInternational, $"International: header text not honoured ({baseInternational} → {withInternational})");

        // Clean up so other tests don't see the header.
        await SetSettings(admin, new { BrandHeaderText = "" });
    }
}
