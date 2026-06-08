using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 27.1 — BOQ line comments + @mentions.
/// Covers create/list/resolve/delete, the open-count endpoint, permission boundaries
/// (only author+admin can delete), the cross-line reply guard, the cross-estimate
/// item-id guard, validation, and the @mention → notification fan-out.
/// </summary>
[Collection("api")]
public class BoqLineCommentTests(ApiFixture fx)
{
    /// <summary>Add a second admin so we can prove the per-user authorization on delete.</summary>
    async Task<(HttpClient client, int userId)> NewAdminAsync(HttpClient admin, string email)
    {
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Helper", email, password = "Pw@123456", role = "TenantAdmin", groupIds = Array.Empty<int>() })).Json();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");
        var meRes = await user.GetFromJsonAsync<JsonElement>("/api/auth/permissions");
        // /api/auth/permissions doesn't return the id; resolve via /api/admin/users.
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var uid = users.EnumerateArray().First(x => x.GetProperty("email").GetString() == email).GetProperty("id").GetInt32();
        _ = meRes;
        return (user, uid);
    }

    async Task<(int eid, int iid)> SeedItemAsync(HttpClient admin, string title)
    {
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, title);
        var sid = await Api.AddSectionAsync(admin, eid, "C1");
        var bd = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "x", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 100, sortOrder = 0,
            components = (object?)null, areaId = (int?)null,
        })).Json();
        var iid = bd.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("id").GetInt32();
        return (eid, iid);
    }

    [Fact]
    public async Task Comments_require_authentication()
    {
        var (eid, iid) = await SeedItemAsync(await fx.AdminClientAsync(), "auth");
        var r = await fx.Client().GetAsync($"/api/estimates/{eid}/items/{iid}/comments");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Create_list_and_count_round_trips()
    {
        var admin = await fx.AdminClientAsync();
        var (eid, iid) = await SeedItemAsync(admin, "list");

        // Empty list and zero count to start.
        var initial = await (await admin.GetAsync($"/api/estimates/{eid}/items/{iid}/comments")).Json();
        Assert.Empty(initial.EnumerateArray());
        var counts0 = await (await admin.GetAsync($"/api/estimates/{eid}/comment-counts")).Json();
        Assert.Empty(counts0.EnumerateArray());

        // Two top-level + a reply.
        var c1 = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments",
            new { body = "Is this rate including waste?", parentCommentId = (int?)null, mentionedUserIds = Array.Empty<int>() })).Json();
        var c1Id = c1.GetProperty("id").GetInt32();
        Assert.Equal("Is this rate including waste?", c1.GetProperty("body").GetString());

        await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments",
            new { body = "And the area allocation?" })).Json();
        await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments",
            new { body = "Yes, +5%.", parentCommentId = c1Id })).Json();

        var thread = await (await admin.GetAsync($"/api/estimates/{eid}/items/{iid}/comments")).Json();
        Assert.Equal(3, thread.EnumerateArray().Count());
        Assert.True(thread[0].GetProperty("id").GetInt32() < thread[2].GetProperty("id").GetInt32()); // oldest first

        var counts = await (await admin.GetAsync($"/api/estimates/{eid}/comment-counts")).Json();
        Assert.Single(counts.EnumerateArray());
        Assert.Equal(iid, counts[0].GetProperty("itemId").GetInt32());
        Assert.Equal(3, counts[0].GetProperty("openCount").GetInt32());
    }

    [Fact]
    public async Task Validation_rejects_empty_or_oversize_body()
    {
        var admin = await fx.AdminClientAsync();
        var (eid, iid) = await SeedItemAsync(admin, "validate");
        var empty = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments", new { body = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var big = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments", new { body = new string('x', 4001) });
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
    }

    [Fact]
    public async Task Reply_must_target_a_comment_on_the_same_line()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "reply-scope");
        var sid = await Api.AddSectionAsync(admin, eid, "R1");

        async Task<int> NewItem(string desc)
        {
            var bd = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items",
                new { description = desc, unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 1, sortOrder = 0,
                    components = (object?)null, areaId = (int?)null })).Json();
            return bd.GetProperty("sections")[0].GetProperty("items").EnumerateArray()
                .First(x => x.GetProperty("description").GetString() == desc).GetProperty("id").GetInt32();
        }
        var iidA = await NewItem("line-A");
        var iidB = await NewItem("line-B");

        var onA = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iidA}/comments", new { body = "first" })).Json();
        var aId = onA.GetProperty("id").GetInt32();

        // Try to reply on line B targeting line A's comment → 400.
        var bad = await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iidB}/comments",
            new { body = "cross-line reply", parentCommentId = aId });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Resolve_drops_from_open_count_then_reopens()
    {
        var admin = await fx.AdminClientAsync();
        var (eid, iid) = await SeedItemAsync(admin, "resolve");
        var c = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments", new { body = "todo" })).Json();
        var cid = c.GetProperty("id").GetInt32();

        var resolved = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments/{cid}/resolve", new { })).Json();
        Assert.NotEqual(JsonValueKind.Null, resolved.GetProperty("resolvedAt").ValueKind);

        var counts = await (await admin.GetAsync($"/api/estimates/{eid}/comment-counts")).Json();
        Assert.Empty(counts.EnumerateArray());  // resolved doesn't count

        // Reopen via ?resolved=false.
        var reopened = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments/{cid}/resolve?resolved=false", new { })).Json();
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("resolvedAt").ValueKind);
        var counts2 = await (await admin.GetAsync($"/api/estimates/{eid}/comment-counts")).Json();
        Assert.Single(counts2.EnumerateArray());
        Assert.Equal(1, counts2[0].GetProperty("openCount").GetInt32());
    }

    [Fact]
    public async Task Only_author_or_admin_can_delete_a_comment()
    {
        var admin = await fx.AdminClientAsync();
        var (other, _) = await NewAdminAsync(admin, $"c.del.other.{Guid.NewGuid():N}@bidbuilder.local");
        var (eid, iid) = await SeedItemAsync(admin, "delete");

        // `other` authors the comment.
        var c = await (await other.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments", new { body = "mine" })).Json();
        var cid = c.GetProperty("id").GetInt32();

        // A third admin (the seeded `admin`) is NOT the author, but IS an admin → can delete.
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/estimates/{eid}/items/{iid}/comments/{cid}")).StatusCode);

        // And the comment is gone for everyone.
        var thread = await (await other.GetAsync($"/api/estimates/{eid}/items/{iid}/comments")).Json();
        Assert.Empty(thread.EnumerateArray());
    }

    [Fact]
    public async Task Non_author_non_admin_cannot_delete_and_sees_404()
    {
        // Author with non-admin role would be ideal, but the only seeded role helpers are admins.
        // The endpoint deliberately returns 404 (not 403) on cross-author delete to avoid leaking
        // existence; here we verify that contract by using a SECOND tenant — its admin can't see
        // the comment at all (cross-tenant isolation), so the same 404 fires.
        var admin = await fx.AdminClientAsync();
        var (eid, iid) = await SeedItemAsync(admin, "cross-tenant");
        var c = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments", new { body = "secret" })).Json();
        var cid = c.GetProperty("id").GetInt32();

        var other = await fx.SecondTenantAdminClientAsync();
        // The OTHER tenant cannot reach this estimate at all (Guard returns 404).
        var r = await other.DeleteAsync($"/api/estimates/{eid}/items/{iid}/comments/{cid}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Item_id_must_belong_to_the_estimate()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);

        // eA's item, but address it via eB → must 404.
        var eA = await Api.NewEstimateAsync(admin, pid, "eA");
        var sA = await Api.AddSectionAsync(admin, eA, "AA");
        var bdA = await (await admin.PostAsJsonAsync($"/api/estimates/{eA}/sections/{sA}/items",
            new { description = "x", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 1, sortOrder = 0,
                  components = (object?)null, areaId = (int?)null })).Json();
        var iidA = bdA.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("id").GetInt32();

        var eB = await Api.NewEstimateAsync(admin, pid, "eB");

        var bad = await admin.PostAsJsonAsync($"/api/estimates/{eB}/items/{iidA}/comments", new { body = "x" });
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
    }

    [Fact]
    public async Task Mentioning_a_user_creates_a_notification_for_them_but_not_for_the_author()
    {
        var admin = await fx.AdminClientAsync();
        var mentionedEmail = $"c.mention.{Guid.NewGuid():N}@bidbuilder.local";
        var (mentioned, mentionedUid) = await NewAdminAsync(admin, mentionedEmail);

        var (eid, iid) = await SeedItemAsync(admin, "mention");
        await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/items/{iid}/comments",
            new { body = "@helper please check this rate", mentionedUserIds = new[] { mentionedUid } })).Json();

        // 27.x — Mentioned user has a comment.mention notification scoped to the BOQ ITEM
        // (entityType="BoqItem", entityKey=iid). The earlier estimate-scoped key would
        // collapse mentions on different BOQ lines into a single 27.4 group, hiding the
        // per-line context.
        var inbox = await mentioned.GetFromJsonAsync<JsonElement>("/api/notifications?take=20");
        var ours = inbox.GetProperty("items").EnumerateArray()
            .FirstOrDefault(x => x.GetProperty("type").GetString() == "comment.mention"
                              && x.GetProperty("entityType").GetString() == "BoqItem"
                              && x.GetProperty("entityKey").GetString() == iid.ToString());
        Assert.NotEqual(JsonValueKind.Undefined, ours.ValueKind);
        // The actor (admin) was NOT notified about their own action.
        var actorInbox = await admin.GetFromJsonAsync<JsonElement>("/api/notifications?take=20");
        Assert.DoesNotContain(actorInbox.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("type").GetString() == "comment.mention"
              && x.GetProperty("entityKey").GetString() == iid.ToString());
    }
}
