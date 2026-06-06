using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.4 — Tests for cross-project search (<c>GET /api/search?q=…</c>).
///
/// Coverage:
///   • Finds a project by a fragment of its code.
///   • Finds an estimate by a fragment of its title (link points at the project).
///   • Finds an assembly by its code.
///   • A one-character query short-circuits to an empty result.
///   • Results are scoped: another tenant can't see this tenant's projects.
/// </summary>
[Collection("api")]
public class SearchTests(ApiFixture fx)
{
    static async Task<JsonElement> SearchAsync(HttpClient c, string q) =>
        await c.GetFromJsonAsync<JsonElement>($"/api/search?q={Uri.EscapeDataString(q)}");

    static IEnumerable<JsonElement> Groups(JsonElement res, string type) =>
        res.GetProperty("groups").EnumerateArray().Where(g => g.GetProperty("type").GetString() == type);

    static IEnumerable<JsonElement> Hits(JsonElement res, string type) =>
        Groups(res, type).SelectMany(g => g.GetProperty("hits").EnumerateArray());

    static bool HasLink(JsonElement res, string type, string link) =>
        Hits(res, type).Any(h => h.GetProperty("link").GetString() == link);

    [Fact]
    public async Task Finds_project_by_code_fragment()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var res = await SearchAsync(admin, "PRJ-2026");
        Assert.True(HasLink(res, "project", $"/projects/{pid}"));
    }

    [Fact]
    public async Task Finds_estimate_by_title_fragment()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "ZqSearchTitleAbc");
        Assert.True(eid > 0);

        var res = await SearchAsync(admin, "ZqSearchTitle");
        Assert.True(HasLink(res, "estimate", $"/projects/{pid}"));
        Assert.Contains(Hits(res, "estimate"), h => h.GetProperty("title").GetString() == "ZqSearchTitleAbc");
    }

    [Fact]
    public async Task Finds_assembly_by_code()
    {
        var admin = await fx.AdminClientAsync();
        var made = await (await admin.PostAsJsonAsync("/api/assemblies",
            new { code = "ASMSEARCHXYZ", name = "Search Probe Assembly", unit = "m3", isActive = true })).Json();
        var aid = made.GetProperty("id").GetInt32();

        var res = await SearchAsync(admin, "ASMSEARCHXYZ");
        Assert.True(HasLink(res, "assembly", $"/assemblies/{aid}"));
    }

    [Fact]
    public async Task Single_character_query_returns_empty()
    {
        var admin = await fx.AdminClientAsync();
        var res = await SearchAsync(admin, "z");
        Assert.Empty(res.GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task Does_not_surface_another_tenants_projects()
    {
        // Ensure the default tenant's project exists.
        var admin = await fx.AdminClientAsync();
        await Api.ProjectIdAsync(admin);

        // A different tenant's admin searching the default project's code sees nothing.
        var other = await fx.SecondTenantAdminClientAsync();
        var res = await SearchAsync(other, "PRJ-2026-001");
        Assert.Empty(Groups(res, "project"));
    }
}
