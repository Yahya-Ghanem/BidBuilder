using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 27.2 — Tests for the live-presence endpoints + in-process PresenceStore.
///
/// Coverage:
///   • Auth required (401)
///   • Heartbeat puts the caller in the list
///   • Two callers both appear in each other's list
///   • Inaccessible (cross-tenant) estimate → 404 (existence hidden, not 403)
///   • Stale entries are pruned on the next List (TTL semantics, via direct service access)
///   • Heartbeat works on a Published revision (AllowWhenFinalised — presence is read-equivalent)
///   • GET returns the same shape as POST (passive observer path)
/// </summary>
[Collection("api")]
public class PresenceTests(ApiFixture fx)
{
    /// <summary>Create another TenantAdmin in the seed tenant and return their authed client.</summary>
    async Task<HttpClient> NewAdminAsync(HttpClient admin, string email)
    {
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Presence Peer", email, password = "Pw@123456", role = "TenantAdmin", groupIds = Array.Empty<int>() })).Json();
        return await fx.AuthedClientAsync(email, "Pw@123456");
    }

    static async Task<JsonElement[]> UsersAsync(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        var bd = await r.Content.ReadFromJsonAsync<JsonElement>();
        return bd.GetProperty("users").EnumerateArray().ToArray();
    }

    [Fact]
    public async Task Anonymous_heartbeat_is_unauthorized()
    {
        var anon = fx.Client();
        var r = await anon.PostAsync("/api/estimates/1/presence", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_lists_the_caller()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "presence-self");

        var users = await UsersAsync(await admin.PostAsync($"/api/estimates/{eid}/presence", content: null));
        Assert.Single(users);
        Assert.Equal("admin@bidbuilder.local", users[0].GetProperty("email").GetString());
        // lastSeenAt is an ISO timestamp string (DateTime serialization).
        Assert.Equal(JsonValueKind.String, users[0].GetProperty("lastSeenAt").ValueKind);
    }

    [Fact]
    public async Task Two_users_both_appear_in_the_list()
    {
        var admin = await fx.AdminClientAsync();
        var peer = await NewAdminAsync(admin, "presence.peer@bidbuilder.local");

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "presence-two");

        (await admin.PostAsync($"/api/estimates/{eid}/presence", content: null)).EnsureSuccessStatusCode();
        var users = await UsersAsync(await peer.PostAsync($"/api/estimates/{eid}/presence", content: null));

        Assert.Equal(2, users.Length);
        var emails = users.Select(u => u.GetProperty("email").GetString()).ToHashSet();
        Assert.Contains("admin@bidbuilder.local", emails);
        Assert.Contains("presence.peer@bidbuilder.local", emails);
    }

    [Fact]
    public async Task Inaccessible_estimate_returns_404_not_403()
    {
        var admin = await fx.AdminClientAsync();
        // A bogus id no caller in this tenant could access — the access guard 404s
        // before the store is touched, hiding existence (a 403 would leak that the
        // id belongs to another tenant).
        var r = await admin.PostAsync("/api/estimates/999999/presence", content: null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_works_on_published_revision()
    {
        var admin = await fx.AdminClientAsync();
        // Clear the publish-approval requirement so the status transition takes effect immediately.
        (await admin.PutAsJsonAsync("/api/settings/", new
        {
            timezone = "UTC", baseCurrency = "AED",
            defaultOverheadPct = 0m, defaultProfitPct = 0m,
            defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            requiredApprovalsToPublish = 0,
        })).EnsureSuccessStatusCode();

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "presence-published");
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();

        // Status-lock would 409 a write on a finalised revision; AllowWhenFinalised
        // exempts presence so the heartbeat still succeeds.
        var r = await admin.PostAsync($"/api/estimates/{eid}/presence", content: null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Single(await UsersAsync(r));
    }

    [Fact]
    public async Task Get_endpoint_returns_same_shape_as_post()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "presence-get");

        (await admin.PostAsync($"/api/estimates/{eid}/presence", content: null)).EnsureSuccessStatusCode();
        var r = await admin.GetAsync($"/api/estimates/{eid}/presence");
        var users = await UsersAsync(r);

        Assert.Single(users);
        Assert.Equal("admin@bidbuilder.local", users[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task Store_prunes_stale_entries_past_ttl()
    {
        // Drive PresenceStore directly so we control the clock — testing the
        // TTL via the HTTP endpoint would need a 90-second sleep.
        using var scope = await fx.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PresenceStore>();
        var t0 = DateTime.UtcNow;
        // Use a unique estimate id so this test doesn't collide with the others'
        // buckets (the singleton is shared across the xUnit collection).
        var eid = 90001 + (int)(DateTime.UtcNow.Ticks % 1000);

        // Both users heartbeat at the same moment t0 — neither stale on the way out.
        store.Touch(eid, userId: 1, "Alice", "alice@x", t0);
        store.Touch(eid, userId: 2, "Bob",   "bob@x",   t0);

        var live = store.List(eid, t0);
        Assert.Equal(2, live.Count);

        // Past the 90 s TTL, every entry is stale — empty list.
        var laterEmpty = store.List(eid, t0 + TimeSpan.FromMinutes(5));
        Assert.Empty(laterEmpty);

        // Bob heartbeats again — Alice stays gone, Bob is alone in the cluster.
        store.Touch(eid, userId: 2, "Bob", "bob@x", t0 + TimeSpan.FromMinutes(5));
        var aloneBob = store.List(eid, t0 + TimeSpan.FromMinutes(5));
        Assert.Single(aloneBob);
        Assert.Equal("Bob", aloneBob[0].Name);
    }
}
