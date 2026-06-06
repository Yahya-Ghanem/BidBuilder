using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.12 coverage: bulk operations over the resource library. The contract
/// estimators rely on:
///
///   1. Bulk deactivate/activate flips IsActive across many resources in one call.
///   2. Bulk delete removes the requested resources but SKIPS any that are
///      referenced by an assembly (those are reported, not orphaned).
///   3. Unknown actions and empty id lists are rejected with 400.
/// </summary>
[Collection("api")]
public class BulkResourceTests(ApiFixture fx)
{
    private static async Task<int> NewLabor(HttpClient c, decimal rate = 50m, bool active = true)
    {
        var created = await (await c.PostAsJsonAsync("/api/resources/labor", new
        {
            code = $"LAB-{System.Guid.NewGuid():N}"[..10], name = "Crew", unit = "hr",
            ratePerHour = rate, isActive = active,
        })).Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Bulk_deactivate_then_activate_flips_IsActive()
    {
        var admin = await fx.AdminClientAsync();
        var a = await NewLabor(admin);
        var b = await NewLabor(admin);

        var deactivate = await admin.PostAsJsonAsync("/api/resources/labor/bulk", new { ids = new[] { a, b }, action = "deactivate" });
        deactivate.EnsureSuccessStatusCode();
        var dr = await deactivate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, dr.GetProperty("updated").GetInt32());

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/resources/labor");
        var byId = list.EnumerateArray().Where(r => r.GetProperty("id").GetInt32() is var i && (i == a || i == b)).ToList();
        Assert.All(byId, r => Assert.False(r.GetProperty("isActive").GetBoolean()));

        var activate = await admin.PostAsJsonAsync("/api/resources/labor/bulk", new { ids = new[] { a, b }, action = "activate" });
        activate.EnsureSuccessStatusCode();
        Assert.Equal(2, (await activate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updated").GetInt32());
    }

    [Fact]
    public async Task Bulk_delete_removes_free_resources()
    {
        var admin = await fx.AdminClientAsync();
        var a = await NewLabor(admin);
        var b = await NewLabor(admin);

        var resp = await admin.PostAsJsonAsync("/api/resources/labor/bulk", new { ids = new[] { a, b }, action = "delete" });
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, r.GetProperty("deleted").GetInt32());
        Assert.Empty(r.GetProperty("skipped").EnumerateArray());

        // Both are gone from the listing.
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/resources/labor");
        Assert.DoesNotContain(list.EnumerateArray(), x => x.GetProperty("id").GetInt32() == a || x.GetProperty("id").GetInt32() == b);
    }

    [Fact]
    public async Task Bulk_delete_skips_resources_used_by_an_assembly()
    {
        var admin = await fx.AdminClientAsync();
        var used = await NewLabor(admin, rate: 80m);
        var free = await NewLabor(admin, rate: 60m);

        // Build an assembly that references `used`.
        var asm = await (await admin.PostAsJsonAsync("/api/assemblies", new
        {
            code = $"ASM-{System.Guid.NewGuid():N}"[..10], name = "Bulk test assembly", unit = "m", isActive = true,
        })).Content.ReadFromJsonAsync<JsonElement>();
        var asmId = asm.GetProperty("id").GetInt32();
        (await admin.PostAsJsonAsync($"/api/assemblies/{asmId}/components", new
        {
            resourceType = "Labor", resourceId = used, factor = 1m, note = (string?)null,
        })).EnsureSuccessStatusCode();

        var resp = await admin.PostAsJsonAsync("/api/resources/labor/bulk", new { ids = new[] { used, free }, action = "delete" });
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, r.GetProperty("deleted").GetInt32());
        var skipped = r.GetProperty("skipped").EnumerateArray().ToList();
        Assert.Single(skipped);
        Assert.Equal(used, skipped[0].GetProperty("id").GetInt32());

        // The free one is gone, the used one survived.
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/resources/labor");
        var ids = list.EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(used, ids);
        Assert.DoesNotContain(free, ids);
    }

    [Fact]
    public async Task Bulk_rejects_unknown_action()
    {
        var admin = await fx.AdminClientAsync();
        var a = await NewLabor(admin);
        var resp = await admin.PostAsJsonAsync("/api/resources/labor/bulk", new { ids = new[] { a }, action = "frobnicate" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Bulk_rejects_empty_id_list()
    {
        var admin = await fx.AdminClientAsync();
        var resp = await admin.PostAsJsonAsync("/api/resources/labor/bulk", new { ids = System.Array.Empty<int>(), action = "deactivate" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
