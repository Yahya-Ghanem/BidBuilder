using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 21.3 — Reusable estimate templates. Save an estimate's structure as a template,
/// then create a fresh Draft estimate from it.
///
/// Coverage:
///   • save → apply reproduces the section/item structure and the bid.
///   • list + delete.
///   • applying a missing template is 404.
///   • estimate-admin is required to manage/apply templates.
/// </summary>
[Collection("api")]
public class EstimateTemplateTests(ApiFixture fx)
{
    /// <summary>Build a tiny estimate (one section, one ad-hoc item qty 2 × rate 100 = 200)
    /// and return (projectId, estimateId).</summary>
    async Task<(int pid, int eid)> SeedEstimateAsync(HttpClient admin)
    {
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "tpl-source");
        var sid = await Api.AddSectionAsync(admin, eid, $"S{Guid.NewGuid():N}".Substring(0, 6));
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Blockwork", unit = "m2", quantity = 2, assemblyId = (int?)null, unitRate = 100, sortOrder = 0,
        })).EnsureSuccessStatusCode();
        return (pid, eid);
    }

    static int SectionCount(JsonElement bd) => bd.GetProperty("sections").GetArrayLength();
    static int ItemCount(JsonElement bd) => bd.GetProperty("sections").EnumerateArray().Sum(s => s.GetProperty("items").GetArrayLength());
    static decimal Bid(JsonElement bd) => bd.GetProperty("bidPrice").GetDecimal();

    [Fact]
    public async Task Save_then_apply_reproduces_structure_and_bid()
    {
        var admin = await fx.AdminClientAsync();
        var (pid, eid) = await SeedEstimateAsync(admin);

        var srcBd = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        Assert.Equal(200m, Bid(srcBd));

        var tpl = await (await admin.PostAsJsonAsync("/api/estimate-templates/",
            new { name = "Warehouse shell", description = "standard", estimateId = eid })).Json();
        Assert.Equal(1, tpl.GetProperty("sectionCount").GetInt32());
        Assert.Equal(1, tpl.GetProperty("itemCount").GetInt32());
        var templateId = tpl.GetProperty("id").GetInt32();

        var summary = await (await admin.PostAsJsonAsync($"/api/projects/{pid}/estimates/from-template",
            new { templateId, title = "From template" })).Json();
        Assert.Equal(200m, summary.GetProperty("bidPrice").GetDecimal());
        var newId = summary.GetProperty("id").GetInt32();
        Assert.NotEqual(eid, newId);

        var newBd = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{newId}");
        Assert.Equal(1, SectionCount(newBd));
        Assert.Equal(1, ItemCount(newBd));
        Assert.Equal(200m, Bid(newBd));
        Assert.Equal("Draft", newBd.GetProperty("status").GetString());
    }

    [Fact]
    public async Task List_then_delete_template()
    {
        var admin = await fx.AdminClientAsync();
        var (_, eid) = await SeedEstimateAsync(admin);
        var name = $"tpl-{Guid.NewGuid():N}";
        var tpl = await (await admin.PostAsJsonAsync("/api/estimate-templates/", new { name, estimateId = eid })).Json();
        var id = tpl.GetProperty("id").GetInt32();

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/estimate-templates");
        Assert.Contains(list.EnumerateArray(), t => t.GetProperty("id").GetInt32() == id);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/estimate-templates/{id}")).StatusCode);

        var after = await admin.GetFromJsonAsync<JsonElement>("/api/estimate-templates");
        Assert.DoesNotContain(after.EnumerateArray(), t => t.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task Apply_missing_template_is_404()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var resp = await admin.PostAsJsonAsync($"/api/projects/{pid}/estimates/from-template", new { templateId = 999999 });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Estimate_admin_is_required()
    {
        var admin = await fx.AdminClientAsync();
        var (pid, eid) = await SeedEstimateAsync(admin);
        var email = $"tpl.plain.{Guid.NewGuid():N}@bidbuilder.local";
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Plain", email, password = "Pw@123456", role = "TenantUser", groupIds = Array.Empty<int>() })).Json();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/estimate-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PostAsJsonAsync("/api/estimate-templates/", new { name = "x", estimateId = eid })).StatusCode);
        // from-template checks project access first, so a non-member can't even see the project (404).
        Assert.Equal(HttpStatusCode.NotFound,
            (await user.PostAsJsonAsync($"/api/projects/{pid}/estimates/from-template", new { templateId = 1 })).StatusCode);
    }
}
