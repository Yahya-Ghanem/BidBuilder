using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 28.2 coverage: BOQ bulk actions. The acceptance bar is "selecting 10 lines
/// and deleting them is one click instead of 10", and the security crux is
/// per-line authz: every item id in the request must already belong to a
/// section under this estimate. One foreign id rejects the WHOLE batch —
/// partial application would let a caller probe id space.
///
/// Coverage:
///   1. Bulk delete removes every line in one request.
///   2. Bulk duplicate clones each line in place, carrying cost components.
///   3. Bulk move relocates every line into the target section.
///   4. Bulk tag sets Area on every line; ClearArea blanks it back to null.
///   5. Per-line authz: an id from a different estimate (same tenant, same
///      project) rejects the whole batch (404) and changes nothing.
///   6. Cross-tenant id is 404, not "ok with zero affected".
///   7. Locked revision (Published) refuses every action with 409.
///   8. Unknown action and empty id list are 400.
/// </summary>
[Collection("api")]
public class BulkBoqTests(ApiFixture fx)
{
    /// <summary>Add a section + N items to an estimate; return (sectionId, itemIds[]).</summary>
    private static async Task<(int sectionId, int[] itemIds)> SeedSectionAsync(
        HttpClient c, int eid, string code, int count)
    {
        var sid = await Api.AddSectionAsync(c, eid, code);
        var itemIds = new List<int>();
        for (int n = 1; n <= count; n++)
        {
            var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
            {
                itemCode = $"I{n}", description = $"line {n}", unit = "m",
                quantity = 1m, unitRate = 100m, sortOrder = n,
            })).Json();
            // The most recent item is the one with the highest sortOrder in this section.
            var section = bd.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("id").GetInt32() == sid);
            var item = section.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("itemCode").GetString() == $"I{n}");
            itemIds.Add(item.GetProperty("id").GetInt32());
        }
        return (sid, itemIds.ToArray());
    }

    private static int ItemCount(JsonElement bd, int sectionId) =>
        bd.GetProperty("sections").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt32() == sectionId)
            .GetProperty("items").EnumerateArray().Count();

    [Fact]
    public async Task Bulk_delete_removes_every_supplied_line_in_one_request()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "bulk-delete");
        var (sid, ids) = await SeedSectionAsync(admin, eid, "BD", 10);

        // ONE click — one request — ten lines gone. This is the acceptance bar.
        var resp = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/bulk", new
        {
            action = "delete", itemIds = ids,
        });
        resp.EnsureSuccessStatusCode();
        var bd = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, ItemCount(bd, sid));
    }

    [Fact]
    public async Task Bulk_duplicate_clones_each_line_in_place()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "bulk-duplicate");
        var (sid, ids) = await SeedSectionAsync(admin, eid, "DUP", 3);

        var resp = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/bulk", new
        {
            action = "duplicate", itemIds = ids,
        });
        resp.EnsureSuccessStatusCode();
        var bd = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // 3 originals + 3 clones.
        Assert.Equal(6, ItemCount(bd, sid));
    }

    [Fact]
    public async Task Bulk_move_relocates_every_line_into_target_section()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "bulk-move");
        var (srcSid, ids) = await SeedSectionAsync(admin, eid, "SRC", 4);
        var dstSid = await Api.AddSectionAsync(admin, eid, "DST");

        var resp = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/bulk", new
        {
            action = "move", itemIds = ids, targetSectionId = dstSid,
        });
        resp.EnsureSuccessStatusCode();
        var bd = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, ItemCount(bd, srcSid));
        Assert.Equal(4, ItemCount(bd, dstSid));
    }

    [Fact]
    public async Task Bulk_move_rejects_target_section_in_a_different_estimate()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eA = await Api.NewEstimateAsync(admin, pid, "move-A");
        var (_, ids) = await SeedSectionAsync(admin, eA, "A", 2);
        var eB = await Api.NewEstimateAsync(admin, pid, "move-B");
        var sB = await Api.AddSectionAsync(admin, eB, "B");

        var resp = await admin.PostAsJsonAsync($"/api/estimates/{eA}/items/bulk", new
        {
            action = "move", itemIds = ids, targetSectionId = sB,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Bulk_tag_sets_then_clears_area_on_every_line()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "bulk-tag");
        var (sid, ids) = await SeedSectionAsync(admin, eid, "T", 3);

        // Create an area for the project (areas are project-scoped).
        var areaResp = await admin.PostAsJsonAsync($"/api/projects/{pid}/areas", new
        {
            name = "Zone-A", kind = "Area", sortOrder = 0,
        });
        areaResp.EnsureSuccessStatusCode();
        var area = await areaResp.Content.ReadFromJsonAsync<JsonElement>();
        var areaId = area.GetProperty("id").GetInt32();

        // Tag all 3.
        var tag = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/bulk", new
        {
            action = "tag", itemIds = ids, targetAreaId = areaId,
        });
        tag.EnsureSuccessStatusCode();
        var bd = await tag.Content.ReadFromJsonAsync<JsonElement>();
        var section = bd.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("id").GetInt32() == sid);
        Assert.All(section.GetProperty("items").EnumerateArray(),
            i => Assert.Equal(areaId, i.GetProperty("areaId").GetInt32()));

        // Now clear them all in one call.
        var clear = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/bulk", new
        {
            action = "tag", itemIds = ids, clearArea = true,
        });
        clear.EnsureSuccessStatusCode();
        var bd2 = await clear.Content.ReadFromJsonAsync<JsonElement>();
        var section2 = bd2.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("id").GetInt32() == sid);
        Assert.All(section2.GetProperty("items").EnumerateArray(),
            i => Assert.Equal(JsonValueKind.Null, i.GetProperty("areaId").ValueKind));
    }

    /// <summary>
    /// Per-line authz: mix one id from a DIFFERENT estimate (same tenant, same
    /// project) into a batch addressed to estimate A. The whole batch must reject
    /// with 404, AND no item — not even the in-scope ones — may be affected.
    /// Partial application would leak existence and let a caller probe id space.
    /// </summary>
    [Fact]
    public async Task Bulk_delete_rejects_when_any_id_belongs_to_a_different_estimate()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eA = await Api.NewEstimateAsync(admin, pid, "authz-A");
        var (sA, idsA) = await SeedSectionAsync(admin, eA, "A", 3);
        var eB = await Api.NewEstimateAsync(admin, pid, "authz-B");
        var (_, idsB) = await SeedSectionAsync(admin, eB, "B", 1);

        var mixed = idsA.Concat(idsB).ToArray();
        var resp = await admin.PostAsJsonAsync($"/api/estimates/{eA}/items/bulk", new
        {
            action = "delete", itemIds = mixed,
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        // No item under estimate A was deleted — confirm by re-reading the breakdown.
        var bd = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eA}");
        Assert.Equal(3, ItemCount(bd, sA));
    }

    /// <summary>
    /// Cross-tenant: a foreign tenant's BOQ ids must look like they don't exist.
    /// 404, never "ok with zero affected" — that leaks tenancy.
    /// </summary>
    [Fact]
    public async Task Bulk_delete_rejects_cross_tenant_id_with_404()
    {
        var t1 = await fx.AdminClientAsync();
        var pid1 = await Api.ProjectIdAsync(t1);
        var e1 = await Api.NewEstimateAsync(t1, pid1, "tenant1");
        var (s1, ids1) = await SeedSectionAsync(t1, e1, "T1", 2);

        var t2 = await fx.SecondTenantAdminClientAsync();
        // Tenant 2 hits tenant 1's estimate id — the access guard surfaces this as 404.
        var resp = await t2.PostAsJsonAsync($"/api/estimates/{e1}/items/bulk", new
        {
            action = "delete", itemIds = ids1,
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        // Nothing changed on tenant 1's side.
        var bd = await t1.GetFromJsonAsync<JsonElement>($"/api/estimates/{e1}");
        Assert.Equal(2, ItemCount(bd, s1));
    }

    /// <summary>
    /// The status-lock filter (Published/Superseded → 409) is fail-closed: the bulk
    /// endpoint is a write and is NOT marked .AllowWhenFinalised(), so a finalised
    /// revision must refuse the call before any per-line work happens.
    /// </summary>
    [Fact]
    public async Task Bulk_delete_on_a_published_revision_returns_409()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "lock");
        var (sid, ids) = await SeedSectionAsync(admin, eid, "L", 2);

        // Publish it (status PUT is the unlock path, AllowWhenFinalised — works on Draft).
        var publish = await Api.PutMetaAsync(admin, eid, new { status = "Published" });
        publish.EnsureSuccessStatusCode();

        var resp = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/bulk", new
        {
            action = "delete", itemIds = ids,
        });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // Lines survived — revert to Draft to confirm.
        await Api.PutMetaAsync(admin, eid, new { status = "Draft" });
        var bd = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        Assert.Equal(2, ItemCount(bd, sid));
    }

    [Fact]
    public async Task Bulk_rejects_unknown_action()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "bad-action");
        var (_, ids) = await SeedSectionAsync(admin, eid, "BAD", 1);
        var resp = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/bulk", new
        {
            action = "frobnicate", itemIds = ids,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Bulk_rejects_empty_id_list()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "empty");
        var resp = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/bulk", new
        {
            action = "delete", itemIds = System.Array.Empty<int>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
