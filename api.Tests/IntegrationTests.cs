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
    public async Task Quantity_times_rate_components_compute_amounts()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "qtyrate");
        var sid = await Api.AddSectionAsync(c, eid, "QR");
        var mat = await Api.TypeIdAsync(c, "MAT");
        var lab = await Api.TypeIdAsync(c, "LAB");
        var wst = await Api.TypeIdAsync(c, "WST");

        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Fitout", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[]
            {
                new { typeId = mat, value = 0, quantity = 100m, rate = 50m },   // material 100 × 50 = 5000
                new { typeId = lab, value = 0, quantity = 40m, rate = 50m },    // manpower 40 h × 50 = 2000
                new { typeId = wst, value = 10m },                              // 10% of 7000 = 700
            },
            areaId = (int?)null,
        })).Json();

        var item = bd.GetProperty("sections")[0].GetProperty("items")[0];
        Assert.Equal(7700m, item.GetProperty("unitRate").GetDecimal());    // 5000 + 2000 + 700
        Assert.Equal(7700m, item.GetProperty("lineTotal").GetDecimal());   // × qty 1
        var matLine = item.GetProperty("components").EnumerateArray().First(x => x.GetProperty("code").GetString() == "MAT");
        Assert.Equal(100m, matLine.GetProperty("quantity").GetDecimal());
        Assert.Equal(50m, matLine.GetProperty("rate").GetDecimal());
        Assert.Equal(5000m, matLine.GetProperty("amount").GetDecimal());

        // negative rate rejected
        var bad = await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "x", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 1,
            components = new object[] { new { typeId = mat, value = 0, quantity = 1m, rate = -5m } }, areaId = (int?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
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

    [Fact]
    public async Task Area_measure_yields_cost_per_unit()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);

        // an area with a 100 m² measure
        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Floor", code = "F", kind = "Area", parentAreaId = (int?)null, sortOrder = 0, quantity = 100m, unit = "m²" })).Json();
        int aid = area.GetProperty("id").GetInt32();
        Assert.Equal(100m, area.GetProperty("quantity").GetDecimal());
        Assert.Equal("m²", area.GetProperty("unit").GetString());

        // 5000 of cost assigned to it → 50 / m²
        var eid = await Api.NewEstimateAsync(c, pid, "measure");
        var sid = await Api.AddSectionAsync(c, eid, "M1");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "slab", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 5000, sortOrder = 0,
            components = (object?)null, areaId = aid,
        });

        var roll = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/areas-rollup");
        var node = roll.GetProperty("areas").EnumerateArray().First(x => x.GetProperty("id").GetInt32() == aid);
        Assert.Equal(5000m, node.GetProperty("rollupTotal").GetDecimal());
        Assert.Equal(50m, node.GetProperty("costPerUnit").GetDecimal());   // 5000 / 100

        // negative quantity rejected
        var bad = await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "x", kind = "Unit", parentAreaId = aid, sortOrder = 0, quantity = -1m, unit = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
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

[Collection("api")]
public class BenchmarkTests(ApiFixture fx)
{
    [Fact]
    public async Task Benchmarks_group_area_cost_per_unit_across_projects()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);

        // an area measured in "key" (e.g. hotel keys), 10 keys
        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Tower", code = "T", kind = "Area", parentAreaId = (int?)null, sortOrder = 0, quantity = 10m, unit = "key" })).Json();
        int aid = area.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "bench");
        var sid = await Api.AddSectionAsync(c, eid, "BM1");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "fitout", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 2000, sortOrder = 0,
            components = (object?)null, areaId = aid,
        });
        // publish so this revision is the representative one for the project
        var bd = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        var rv = bd.GetProperty("rowVersion").GetString();
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/estimates/{eid}")
        { Content = JsonContent.Create(new { title = "bench", status = "Published", secondaryCurrency = (string?)null }) };
        put.Headers.TryAddWithoutValidation("If-Match", rv);
        (await c.SendAsync(put)).EnsureSuccessStatusCode();

        var res = await c.GetFromJsonAsync<JsonElement>("/api/benchmarks");
        var group = res.GetProperty("units").EnumerateArray().First(u => u.GetProperty("unit").GetString() == "key");
        // 2000 / 10 keys = 200 per key
        Assert.Contains(group.GetProperty("points").EnumerateArray(),
            p => p.GetProperty("costPerUnit").GetDecimal() == 200m);
        Assert.True(group.GetProperty("max").GetDecimal() >= 200m);
    }

    [Fact]
    public async Task Benchmarks_require_authentication()
    {
        var r = await fx.Client().GetAsync("/api/benchmarks");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

[Collection("api")]
public class CloneRoomTests(ApiFixture fx)
{
    [Fact]
    public async Task Clone_room_duplicates_unit_and_its_activities()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var unit = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "CloneSrc", code = "CS", kind = "Unit", parentAreaId = (int?)null, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json();
        int aid = unit.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "clone");
        var sid = await Api.AddSectionAsync(c, eid, "ACT");
        var mat = await Api.TypeIdAsync(c, "MAT");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Plastering", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[] { new { typeId = mat, value = 0, quantity = 10m, rate = 5m } }, areaId = aid,   // 10×5 = 50
        });

        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/areas/{aid}/clone", new { name = "CloneSrc copy" })).Json();
        var items = bd.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray()).ToList();
        Assert.Equal(2, items.Count);   // original + clone

        var areas = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}/areas");
        var copy = areas.EnumerateArray().First(a => a.GetProperty("name").GetString() == "CloneSrc copy");
        int copyId = copy.GetProperty("id").GetInt32();
        Assert.Equal("Unit", copy.GetProperty("kind").GetString());
        Assert.Contains(items, it => it.GetProperty("areaId").ValueKind == JsonValueKind.Number
                                     && it.GetProperty("areaId").GetInt32() == copyId
                                     && it.GetProperty("lineTotal").GetDecimal() == 50m);

        // cleanup (estimate first so the areas are no longer referenced by items)
        await c.DeleteAsync($"/api/projects/{pid}/estimates/{eid}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{copyId}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{aid}");
    }

    [Fact]
    public async Task Clone_subarea_duplicates_the_whole_subtree()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        // Apartment (sub-area) → Room (unit) with an activity
        var apt = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Apt-Z", code = "AZ", kind = "SubArea", parentAreaId = (int?)null, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json();
        int aptId = apt.GetProperty("id").GetInt32();
        var room = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Room-Z", code = "RZ", kind = "Unit", parentAreaId = aptId, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json();
        int roomId = room.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "clonesub");
        var sid = await Api.AddSectionAsync(c, eid, "ACT");
        var mat = await Api.TypeIdAsync(c, "MAT");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Tiling", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[] { new { typeId = mat, value = 0, quantity = 4m, rate = 25m } }, areaId = roomId,   // 100
        });

        // clone the apartment (subtree)
        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/areas/{aptId}/clone", new { name = "Apt-Z copy" })).Json();
        var items = bd.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("items").EnumerateArray()).ToList();
        Assert.Equal(2, items.Count);   // tiling original + cloned

        var areas = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}/areas")).EnumerateArray().ToList();
        var aptCopy = areas.First(a => a.GetProperty("name").GetString() == "Apt-Z copy");
        int aptCopyId = aptCopy.GetProperty("id").GetInt32();
        // a cloned child unit "Room-Z" now hangs under the apartment copy
        var roomCopy = areas.First(a => a.GetProperty("name").GetString() == "Room-Z"
                                        && a.GetProperty("parentAreaId").ValueKind == JsonValueKind.Number
                                        && a.GetProperty("parentAreaId").GetInt32() == aptCopyId);
        int roomCopyId = roomCopy.GetProperty("id").GetInt32();
        Assert.Contains(items, it => it.GetProperty("areaId").ValueKind == JsonValueKind.Number
                                     && it.GetProperty("areaId").GetInt32() == roomCopyId
                                     && it.GetProperty("lineTotal").GetDecimal() == 100m);

        // cleanup: estimate, then children before parents
        await c.DeleteAsync($"/api/projects/{pid}/estimates/{eid}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{roomCopyId}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{aptCopyId}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{roomId}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{aptId}");
    }
}

[Collection("api")]
public class ActivityCatalogTests(ApiFixture fx)
{
    [Fact]
    public async Task Builtin_activities_are_seeded()
    {
        var c = await fx.AdminClientAsync();
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/activities");
        var names = arr.EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Contains("Block work", names);
        Assert.Contains("Plastering", names);
        Assert.All(arr.EnumerateArray().Where(a => a.GetProperty("name").GetString() == "Block work"),
            a => Assert.True(a.GetProperty("builtin").GetBoolean()));
    }

    [Fact]
    public async Task Add_user_activity_then_dup_and_builtin_delete_conflicts()
    {
        var c = await fx.AdminClientAsync();

        var created = await (await c.PostAsJsonAsync("/api/activities", new { name = "Roofing", sortOrder = 0, isActive = true })).Json();
        int id = created.GetProperty("id").GetInt32();
        Assert.False(created.GetProperty("builtin").GetBoolean());

        // duplicate name (case-insensitive) → 409
        var dup = await c.PostAsJsonAsync("/api/activities", new { name = "roofing", sortOrder = 0, isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // built-in cannot be deleted → 409
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/activities");
        int builtinId = arr.EnumerateArray().First(a => a.GetProperty("builtin").GetBoolean()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/activities/{builtinId}")).StatusCode);

        // the user activity deletes cleanly (restore baseline)
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/activities/{id}")).StatusCode);
    }

    [Fact]
    public async Task Activities_require_authentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client().GetAsync("/api/activities")).StatusCode);
    }
}

[Collection("api")]
public class UserManagementTests(ApiFixture fx)
{
    [Fact]
    public async Task Admin_endpoints_reject_anonymous()
    {
        var r = await fx.Client().GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Create_team_grant_permission_create_user_and_login()
    {
        var c = await fx.AdminClientAsync();

        // 1. create a team
        var grp = await (await c.PostAsJsonAsync("/api/admin/groups",
            new { code = "QA-TEAM", name = "QA Team", description = (string?)null })).Json();
        int gid = grp.GetProperty("id").GetInt32();

        // 2. grant it boq view+add via the permission grid
        var mods = await c.GetFromJsonAsync<JsonElement>("/api/admin/modules");
        int boq = mods.EnumerateArray().First(m => m.GetProperty("code").GetString() == "boq").GetProperty("id").GetInt32();
        var perm = await (await c.PutAsJsonAsync($"/api/admin/groups/{gid}/permissions",
            new { permissions = new[] { new { moduleId = boq, canView = true, canAdd = true, canEdit = false, canDelete = false } } })).Json();
        Assert.Equal(1, perm.GetProperty("permissions").GetArrayLength());

        // 3. create a user in that team
        const string email = "qa.user@bidbuilder.local";
        var u = await (await c.PostAsJsonAsync("/api/admin/users",
            new { name = "QA User", email, password = "Qa@123456", role = "TenantUser", groupIds = new[] { gid } })).Json();
        int uid = u.GetProperty("id").GetInt32();
        Assert.Single(u.GetProperty("groups").EnumerateArray());

        // 4. the new account can log in
        var login = await fx.Client().PostAsJsonAsync("/api/auth/login", new { email, password = "Qa@123456" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // 5. duplicate email is a 409
        var dup = await c.PostAsJsonAsync("/api/admin/users",
            new { name = "Dup", email, password = "Qa@123456", role = "TenantUser", groupIds = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // 6. reset password, then log in with the new one
        var reset = await c.PostAsJsonAsync($"/api/admin/users/{uid}/reset-password", new { password = "NewPw@123" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var relogin = await fx.Client().PostAsJsonAsync("/api/auth/login", new { email, password = "NewPw@123" });
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);

        // cleanup so reruns / other tests stay clean
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/admin/users/{uid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/admin/groups/{gid}")).StatusCode);
    }

    [Fact]
    public async Task Builtin_team_and_last_admin_are_protected()
    {
        var c = await fx.AdminClientAsync();

        // built-in ADMINS team cannot be deleted
        var groups = await c.GetFromJsonAsync<JsonElement>("/api/admin/groups");
        int builtin = groups.EnumerateArray().First(g => g.GetProperty("isBuiltIn").GetBoolean()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/admin/groups/{builtin}")).StatusCode);

        // seeded admin is the only admin → cannot be deleted nor self-deactivated
        var users = await c.GetFromJsonAsync<JsonElement>("/api/admin/users");
        int adminId = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == "admin@bidbuilder.local").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/admin/users/{adminId}")).StatusCode);

        var deact = await c.PutAsJsonAsync($"/api/admin/users/{adminId}",
            new { name = "Demo Admin", email = "admin@bidbuilder.local", role = "TenantAdmin", isActive = false, groupIds = new[] { builtin } });
        Assert.Equal(HttpStatusCode.Conflict, deact.StatusCode);
    }
}
