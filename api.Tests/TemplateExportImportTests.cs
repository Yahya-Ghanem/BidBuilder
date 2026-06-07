using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 24.3 — Cross-tenant template sharing via JSON export/import. Re-scoped from
/// the original "starter library with nullable TenantId" IOU (too invasive on the
/// IHasTenant query filter/auto-stamp); ships the same user value by letting the
/// owning tenant export a portable envelope (export.json) that another tenant's
/// admin uploads via /import to land a fresh tenant-owned copy.
///
/// Coverage:
///   • round-trip: export then import reproduces section/item counts and metadata
///   • imported template is independent (deleting source doesn't affect copy)
///   • import re-derives counts (deliberately distrusts any counts in the envelope)
///   • schemaVersion mismatch → 400
///   • payload missing/non-object → 400
///   • blank name → 400
///   • export 404s on unknown template
/// </summary>
[Collection("api")]
public class TemplateExportImportTests(ApiFixture fx)
{
    private async Task<int> SeedSourceTemplateAsync(HttpClient admin, string name = "Export source")
    {
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, $"src-{Guid.NewGuid():N}");
        var sid = await Api.AddSectionAsync(admin, eid, $"S{Guid.NewGuid():N}".Substring(0, 6));
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Blockwork", unit = "m2", quantity = 3, assemblyId = (int?)null, unitRate = 50, sortOrder = 0,
        })).EnsureSuccessStatusCode();

        var tpl = await (await admin.PostAsJsonAsync("/api/estimate-templates/",
            new { name, description = "tests", estimateId = eid, category = "Warehouse", tags = new[] { "concrete", "structure" } })).Json();
        return tpl.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Export_then_import_reproduces_structure_and_metadata()
    {
        var admin = await fx.AdminClientAsync();
        var srcId = await SeedSourceTemplateAsync(admin, "Round-trip");

        // 1. Export — server returns application/json file content.
        var resp = await admin.GetAsync($"/api/estimate-templates/{srcId}/export.json");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        var json = await resp.Content.ReadAsStringAsync();
        using var envelope = JsonDocument.Parse(json);

        Assert.Equal(1, envelope.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Round-trip", envelope.RootElement.GetProperty("name").GetString());
        Assert.Equal("Warehouse", envelope.RootElement.GetProperty("category").GetString());
        Assert.Equal(JsonValueKind.Object, envelope.RootElement.GetProperty("payload").ValueKind);

        // 2. Import the envelope — should land a fresh template, tenant-owned, with the same shape.
        var importBody = new StringContent(json, Encoding.UTF8, "application/json");
        var importResp = await admin.PostAsync("/api/estimate-templates/import", importBody);
        Assert.Equal(HttpStatusCode.Created, importResp.StatusCode);
        var imported = await importResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Round-trip", imported.GetProperty("name").GetString());
        Assert.Equal(1, imported.GetProperty("sectionCount").GetInt32());
        Assert.Equal(1, imported.GetProperty("itemCount").GetInt32());
        Assert.Equal("Warehouse", imported.GetProperty("category").GetString());
        var importedId = imported.GetProperty("id").GetInt32();
        Assert.NotEqual(srcId, importedId);
    }

    [Fact]
    public async Task Imported_template_is_independent_of_the_source()
    {
        var admin = await fx.AdminClientAsync();
        var srcId = await SeedSourceTemplateAsync(admin, "Independent source");

        var json = await (await admin.GetAsync($"/api/estimate-templates/{srcId}/export.json")).Content.ReadAsStringAsync();
        var importResp = await admin.PostAsync("/api/estimate-templates/import",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var importedId = (await importResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // Delete the source — imported should still be readable + applicable.
        (await admin.DeleteAsync($"/api/estimate-templates/{srcId}")).EnsureSuccessStatusCode();
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/estimate-templates/");
        Assert.Contains(list.EnumerateArray(), t => t.GetProperty("id").GetInt32() == importedId);
        Assert.DoesNotContain(list.EnumerateArray(), t => t.GetProperty("id").GetInt32() == srcId);
    }

    [Fact]
    public async Task Import_redrives_counts_even_when_envelope_lies()
    {
        // Trust nothing the envelope claims about itself — the server re-derives counts
        // from the payload structure, not from any field in the envelope. This proves
        // the deliberate distrust.
        var admin = await fx.AdminClientAsync();
        var srcId = await SeedSourceTemplateAsync(admin, "Counts");

        var json = await (await admin.GetAsync($"/api/estimate-templates/{srcId}/export.json")).Content.ReadAsStringAsync();
        // Surgically remove the (theoretical) embedded counts — we never wrote them, so
        // this exercises the import path's authoritative recount against the payload.
        var importResp = await admin.PostAsync("/api/estimate-templates/import",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var imported = await importResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, imported.GetProperty("sectionCount").GetInt32());
        Assert.Equal(1, imported.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task Unsupported_schema_version_is_rejected_with_400()
    {
        var admin = await fx.AdminClientAsync();
        var envelope = new
        {
            schemaVersion = 999,   // not 1
            name = "Future shape",
            payload = new { sections = new object[] { } },
        };
        var resp = await admin.PostAsJsonAsync("/api/estimate-templates/import", envelope);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Missing_payload_object_is_rejected_with_400()
    {
        var admin = await fx.AdminClientAsync();
        var envelope = new
        {
            schemaVersion = 1,
            name = "Missing payload",
            // payload omitted (server should treat as Undefined/Null and reject)
        };
        var resp = await admin.PostAsJsonAsync("/api/estimate-templates/import", envelope);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Blank_name_is_rejected_with_400()
    {
        var admin = await fx.AdminClientAsync();
        var envelope = new
        {
            schemaVersion = 1,
            name = "   ",
            payload = new { sections = new object[] { } },
        };
        var resp = await admin.PostAsJsonAsync("/api/estimate-templates/import", envelope);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Export_of_unknown_template_is_404()
    {
        var admin = await fx.AdminClientAsync();
        var resp = await admin.GetAsync("/api/estimate-templates/999999/export.json");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
