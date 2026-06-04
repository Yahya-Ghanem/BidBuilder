using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>Small helpers to resolve seeded ids and create scratch fixtures over HTTP.</summary>
internal static class Api
{
    public static async Task<JsonElement> Json(this HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<int> ProjectIdAsync(HttpClient c)
    {
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/projects");
        return arr.EnumerateArray().First(p => p.GetProperty("code").GetString() == "PRJ-2026-001").GetProperty("id").GetInt32();
    }

    public static async Task<int> FirstEstimateIdAsync(HttpClient c, int pid)
    {
        var arr = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}/estimates");
        return arr.EnumerateArray().First().GetProperty("id").GetInt32();
    }

    public static async Task<int> NewEstimateAsync(HttpClient c, int pid, string title)
    {
        var bd = await (await c.PostAsJsonAsync($"/api/projects/{pid}/estimates", new { title })).Json();
        return bd.GetProperty("id").GetInt32();
    }

    public static async Task<int> AddSectionAsync(HttpClient c, int eid, string code)
    {
        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections", new { code, title = "T", sortOrder = 0 })).Json();
        return bd.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("code").GetString() == code).GetProperty("id").GetInt32();
    }

    public static async Task<int> TypeIdAsync(HttpClient c, string code)
    {
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/cost-components");
        return arr.EnumerateArray().First(t => t.GetProperty("code").GetString() == code).GetProperty("id").GetInt32();
    }
}

[Collection("api")]
public class HealthAndAuthTests(ApiFixture fx)
{
    [Fact]
    public async Task Healthz_returns_ok()
    {
        var r = await fx.Client().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Projects_require_authentication()
    {
        var r = await fx.Client().GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Admin_sees_seeded_sample_project()
    {
        var c = await fx.AdminClientAsync();
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/projects");
        var codes = arr.EnumerateArray().Select(p => p.GetProperty("code").GetString());
        Assert.Contains("PRJ-2026-001", codes);
    }

    [Fact]
    public async Task Admin_permissions_are_all_true()
    {
        var c = await fx.AdminClientAsync();
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/permissions");
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
    }
}

[Collection("api")]
public class CostBuildupTests(ApiFixture fx)
{
    [Fact]
    public async Task Default_cost_types_are_seeded()
    {
        var c = await fx.AdminClientAsync();
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/cost-components");
        var codes = arr.EnumerateArray().Select(t => t.GetProperty("code").GetString()).ToList();
        Assert.Contains("MAT", codes);
        Assert.Contains("WST", codes);
    }

    [Fact]
    public async Task Buildup_computes_rate_and_line_total()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "buildup");
        var sid = await Api.AddSectionAsync(c, eid, "B1");
        var mat = await Api.TypeIdAsync(c, "MAT");
        var wst = await Api.TypeIdAsync(c, "WST");

        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Wall", unit = "m2", quantity = 2, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[] { new { typeId = mat, value = 100 }, new { typeId = wst, value = 10 } },
            areaId = (int?)null,
        })).Json();

        var item = bd.GetProperty("sections")[0].GetProperty("items")[0];
        Assert.Equal(110m, item.GetProperty("unitRate").GetDecimal());   // 100 + 10% of 100
        Assert.Equal(220m, item.GetProperty("lineTotal").GetDecimal());  // ×2
        Assert.Equal(2, item.GetProperty("components").GetArrayLength());
    }

    [Fact]
    public async Task Duplicate_code_and_builtin_delete_are_conflicts()
    {
        var c = await fx.AdminClientAsync();
        var dup = await c.PostAsJsonAsync("/api/cost-components", new { code = "MAT", name = "dup", calcKind = "Amount", sortOrder = 9, isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var matId = await Api.TypeIdAsync(c, "MAT");
        var del = await c.DeleteAsync($"/api/cost-components/{matId}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
    }
}

[Collection("api")]
public class AreaRollupTests(ApiFixture fx)
{
    [Fact]
    public async Task Item_totals_escalate_up_the_area_tree()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);

        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas", new { name = "Bldg", code = "B", kind = "Area", parentAreaId = (int?)null, sortOrder = 0 })).Json();
        int aid = area.GetProperty("id").GetInt32();
        var unit = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas", new { name = "Unit", kind = "Unit", parentAreaId = aid, sortOrder = 0 })).Json();
        int uid = unit.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "area");
        var sid = await Api.AddSectionAsync(c, eid, "A1");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "x", unit = "no", quantity = 3, assemblyId = (int?)null, unitRate = 200, sortOrder = 0,
            components = (object?)null, areaId = uid,
        });

        var roll = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/areas-rollup");
        var node = roll.GetProperty("areas").EnumerateArray().First(x => x.GetProperty("id").GetInt32() == aid);
        Assert.Equal(600m, node.GetProperty("rollupTotal").GetDecimal());     // 200 × 3 escalated to the parent
        Assert.Equal(600m, roll.GetProperty("assignedTotal").GetDecimal());

        // delete guard: parent has a child
        var del = await c.DeleteAsync($"/api/projects/{pid}/areas/{aid}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
    }
}

[Collection("api")]
public class ConcurrencyTests(ApiFixture fx)
{
    [Fact]
    public async Task Stale_if_match_yields_409()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "concur");

        var bd = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        var stale = bd.GetProperty("rowVersion").GetString();

        // A mutation bumps the estimate's xmin (recompute touches the row).
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections", new { code = "z", title = "z", sortOrder = 0 });

        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/estimates/{eid}")
        {
            Content = JsonContent.Create(new { title = "renamed", status = (string?)null, secondaryCurrency = (string?)null }),
        };
        req.Headers.TryAddWithoutValidation("If-Match", stale);
        var resp = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }
}

[Collection("api")]
public class ExportTests(ApiFixture fx)
{
    [Fact]
    public async Task Csv_export_returns_priced_boq()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.FirstEstimateIdAsync(c, pid);
        var r = await c.GetAsync($"/api/estimates/{eid}/export.csv");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/csv", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Section Code,Section Title,Item Code", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Xlsx_export_returns_spreadsheet()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.FirstEstimateIdAsync(c, pid);
        var r = await c.GetAsync($"/api/estimates/{eid}/export.xlsx");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", r.Content.Headers.ContentType?.MediaType);
    }
}
