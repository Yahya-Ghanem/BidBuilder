using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>27.5 — per-user project favourites + recents (<c>/api/preferences</c>).</summary>
[Collection("api")]
public class UserPreferenceTests(ApiFixture fx)
{
    static int[] Pinned(JsonElement e) => e.GetProperty("pinnedProjectIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
    static int[] Recent(JsonElement e) => e.GetProperty("recentProjectIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();

    [Fact]
    public async Task Preferences_require_authentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client().GetAsync("/api/preferences")).StatusCode);
    }

    [Fact]
    public async Task Default_preferences_are_empty()
    {
        var c = await fx.AuthedClientAsync(await NewUserEmail(), "Pw@123456");
        var pref = await (await c.GetAsync("/api/preferences")).Json();
        Assert.Empty(Pinned(pref));
        Assert.Empty(Recent(pref));
    }

    [Fact]
    public async Task Pin_then_unpin_toggles_the_list()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);

        var afterPin = await (await c.PutAsJsonAsync("/api/preferences/pinned", new { projectId = pid, pinned = true })).Json();
        Assert.Contains(pid, Pinned(afterPin));

        // Idempotent: pinning again does not duplicate.
        var again = await (await c.PutAsJsonAsync("/api/preferences/pinned", new { projectId = pid, pinned = true })).Json();
        Assert.Single(Pinned(again), x => x == pid);

        var afterUnpin = await (await c.PutAsJsonAsync("/api/preferences/pinned", new { projectId = pid, pinned = false })).Json();
        Assert.DoesNotContain(pid, Pinned(afterUnpin));
    }

    [Fact]
    public async Task Recent_is_most_recent_first_and_deduped()
    {
        // Fresh user so the recent list is not polluted by other tests' admin activity.
        var c = await fx.AuthedClientAsync(await NewUserEmail(), "Pw@123456");
        var admin = await fx.AdminClientAsync();
        var p1 = await Api.ProjectIdAsync(admin);
        var p2 = (await (await admin.PostAsJsonAsync("/api/projects",
            new { name = "Recent-" + Guid.NewGuid().ToString("N")[..6] })).Json()).GetProperty("id").GetInt32();

        await c.PostAsJsonAsync("/api/preferences/recent", new { projectId = p1 });
        await c.PostAsJsonAsync("/api/preferences/recent", new { projectId = p2 });
        var pref = await (await c.PostAsJsonAsync("/api/preferences/recent", new { projectId = p1 })).Json();  // revisit p1

        var recent = Recent(pref);
        Assert.Equal(p1, recent[0]);                              // most-recent first
        Assert.Equal(p2, recent[1]);
        Assert.Equal(recent.Length, recent.Distinct().Count());   // no duplicates
    }

    [Fact]
    public async Task Preferences_are_isolated_per_user()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        await admin.PutAsJsonAsync("/api/preferences/pinned", new { projectId = pid, pinned = true });

        // A different user sees their OWN (empty) pins, not the admin's.
        var other = await fx.AuthedClientAsync(await NewUserEmail(), "Pw@123456");
        var pref = await (await other.GetAsync("/api/preferences")).Json();
        Assert.DoesNotContain(pid, Pinned(pref));
    }

    [Fact]
    public async Task Pinning_an_inaccessible_project_is_404()
    {
        var c = await fx.AdminClientAsync();
        var r = await c.PutAsJsonAsync("/api/preferences/pinned", new { projectId = 999_999, pinned = true });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Recording_an_inaccessible_project_is_404()
    {
        var c = await fx.AdminClientAsync();
        var r = await c.PostAsJsonAsync("/api/preferences/recent", new { projectId = 999_999 });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    async Task<string> NewUserEmail()
    {
        var admin = await fx.AdminClientAsync();
        var email = $"pref-{Guid.NewGuid():N}@bidbuilder.local";
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Pref User", email, password = "Pw@123456", role = "TenantAdmin", groupIds = Array.Empty<int>() })).Json();
        return email;
    }
}
