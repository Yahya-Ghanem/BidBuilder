using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 22.3 — Estimate templates become a browsable library: category + tags + search filters,
/// and a tenant-admin "featured" pin that sorts a template to the top of the picker. Within
/// the tenant only (the cross-tenant platform-starter variant is a separate future increment).
/// </summary>
[Collection("api")]
public class TemplateLibraryTests(ApiFixture fx)
{
    /// <summary>Seed a tiny estimate and save it as a template with the given library metadata.
    /// Returns the new template id.</summary>
    async Task<int> SaveTemplateAsync(HttpClient admin, string name, string? category = null, string[]? tags = null)
    {
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, $"src-{Guid.NewGuid():N}".Substring(0, 10));
        var sid = await Api.AddSectionAsync(admin, eid, $"S{Guid.NewGuid():N}".Substring(0, 6));
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Blockwork", unit = "m2", quantity = 2, assemblyId = (int?)null, unitRate = 100, sortOrder = 0,
        })).EnsureSuccessStatusCode();

        var tpl = await (await admin.PostAsJsonAsync("/api/estimate-templates/",
            new { name, estimateId = eid, category, tags })).Json();
        return tpl.GetProperty("id").GetInt32();
    }

    static List<int> Ids(JsonElement list) => list.EnumerateArray().Select(t => t.GetProperty("id").GetInt32()).ToList();

    [Fact]
    public async Task Save_captures_category_tags_and_author()
    {
        var admin = await fx.AdminClientAsync();
        var name = $"Meta{Guid.NewGuid():N}".Substring(0, 16);
        var id = await SaveTemplateAsync(admin, name, category: "Warehouse", tags: new[] { "concrete", "framing" });

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/estimate-templates?search={name}");
        var row = list.EnumerateArray().First(t => t.GetProperty("id").GetInt32() == id);

        Assert.Equal("Warehouse", row.GetProperty("category").GetString());
        var tags = row.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("concrete", tags);
        Assert.Contains("framing", tags);
        Assert.False(row.GetProperty("isFeatured").GetBoolean());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("createdByName").GetString()));
    }

    [Fact]
    public async Task Category_filter_returns_only_matching()
    {
        var admin = await fx.AdminClientAsync();
        var cat = $"Cat{Guid.NewGuid():N}".Substring(0, 11);
        var inCat = await SaveTemplateAsync(admin, "InCat", category: cat);
        var other = await SaveTemplateAsync(admin, "Other", category: "Misc");

        var ids = Ids(await admin.GetFromJsonAsync<JsonElement>($"/api/estimate-templates?category={cat}"));
        Assert.Contains(inCat, ids);
        Assert.DoesNotContain(other, ids);
    }

    [Fact]
    public async Task Tag_filter_is_exact_membership_not_substring()
    {
        var admin = await fx.AdminClientAsync();
        var uniq = Guid.NewGuid().ToString("N").Substring(0, 8);
        var t1 = await SaveTemplateAsync(admin, "T1", tags: new[] { $"steel{uniq}" });
        var t2 = await SaveTemplateAsync(admin, "T2", tags: new[] { $"steel{uniq}work" });

        var ids = Ids(await admin.GetFromJsonAsync<JsonElement>($"/api/estimate-templates?tag=steel{uniq}"));
        Assert.Contains(t1, ids);
        Assert.DoesNotContain(t2, ids);   // "steelXwork" must NOT match the exact tag "steelX"
    }

    [Fact]
    public async Task Search_matches_name_substring()
    {
        var admin = await fx.AdminClientAsync();
        var name = $"Searchable{Guid.NewGuid():N}".Substring(0, 18);
        var id = await SaveTemplateAsync(admin, name);

        var ids = Ids(await admin.GetFromJsonAsync<JsonElement>($"/api/estimate-templates?search={name}"));
        Assert.Equal(new[] { id }, ids);
    }

    [Fact]
    public async Task Featured_templates_sort_to_the_top()
    {
        var admin = await fx.AdminClientAsync();
        var cat = $"Ord{Guid.NewGuid():N}".Substring(0, 11);
        var a = await SaveTemplateAsync(admin, "Alpha", category: cat);
        var b = await SaveTemplateAsync(admin, "Beta", category: cat);   // newer → before a by default

        var before = Ids(await admin.GetFromJsonAsync<JsonElement>($"/api/estimate-templates?category={cat}"));
        Assert.Equal(new[] { b, a }, before);

        (await admin.PutAsJsonAsync($"/api/estimate-templates/{a}/featured", new { featured = true })).EnsureSuccessStatusCode();

        var after = Ids(await admin.GetFromJsonAsync<JsonElement>($"/api/estimate-templates?category={cat}"));
        Assert.Equal(a, after[0]);   // pinned to the top despite being older
    }

    [Fact]
    public async Task Featuring_is_tenant_admin_only()
    {
        var admin = await fx.AdminClientAsync();
        var id = await SaveTemplateAsync(admin, "ToFeature");

        var email = $"tpl.feat.{Guid.NewGuid():N}@bidbuilder.local";
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Plain", email, password = "Pw@123456", role = "TenantUser", groupIds = Array.Empty<int>() })).Json();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");

        var resp = await user.PutAsJsonAsync($"/api/estimate-templates/{id}/featured", new { featured = true });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Categories_endpoint_lists_distinct_in_use()
    {
        var admin = await fx.AdminClientAsync();
        var cat = $"Distinct{Guid.NewGuid():N}".Substring(0, 16);
        await SaveTemplateAsync(admin, "C1", category: cat);
        await SaveTemplateAsync(admin, "C2", category: cat);   // same category twice → one entry

        var cats = await admin.GetFromJsonAsync<List<string>>("/api/estimate-templates/categories");
        Assert.Single(cats, c => c == cat);
    }

    [Fact]
    public async Task Metadata_edit_updates_name_category_and_tags()
    {
        var admin = await fx.AdminClientAsync();
        var id = await SaveTemplateAsync(admin, "Editable", category: "Old", tags: new[] { "a" });

        var resp = await admin.PutAsJsonAsync($"/api/estimate-templates/{id}",
            new { name = "Renamed", description = "d", category = "New", tags = new[] { "tagx" } });
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Json();

        Assert.Equal("Renamed", dto.GetProperty("name").GetString());
        Assert.Equal("New", dto.GetProperty("category").GetString());
        Assert.Contains("tagx", dto.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }
}
