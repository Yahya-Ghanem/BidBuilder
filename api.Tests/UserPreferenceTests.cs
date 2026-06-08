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
    static string Theme(JsonElement e)  => e.GetProperty("theme").GetString()!;

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
        // 28.1 — every fresh user starts on "system" so the frontend can defer to
        // the OS prefers-color-scheme media query until the user picks otherwise.
        Assert.Equal("system", Theme(pref));
    }

    // 28.1 — round-trip the theme setter for each accepted value; PUT must return
    // the updated DTO so the frontend can apply it without a follow-up GET.
    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Theme_round_trips(string theme)
    {
        var c = await fx.AuthedClientAsync(await NewUserEmail(), "Pw@123456");
        var put = await (await c.PutAsJsonAsync("/api/preferences/theme", new { theme })).Json();
        Assert.Equal(theme, Theme(put));
        var get = await (await c.GetAsync("/api/preferences")).Json();
        Assert.Equal(theme, Theme(get));
    }

    [Fact]
    public async Task Theme_setter_normalises_case()
    {
        var c = await fx.AuthedClientAsync(await NewUserEmail(), "Pw@123456");
        var put = await (await c.PutAsJsonAsync("/api/preferences/theme", new { theme = "DARK" })).Json();
        Assert.Equal("dark", Theme(put));
    }

    [Fact]
    public async Task Theme_setter_rejects_unknown_value()
    {
        var c = await fx.AuthedClientAsync(await NewUserEmail(), "Pw@123456");
        var r = await c.PutAsJsonAsync("/api/preferences/theme", new { theme = "neon" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Theme_setter_requires_auth()
    {
        var r = await fx.Client().PutAsJsonAsync("/api/preferences/theme", new { theme = "dark" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Theme_is_isolated_per_user()
    {
        var alice = await fx.AuthedClientAsync(await NewUserEmail(), "Pw@123456");
        var bob   = await fx.AuthedClientAsync(await NewUserEmail(), "Pw@123456");
        (await alice.PutAsJsonAsync("/api/preferences/theme", new { theme = "dark" })).EnsureSuccessStatusCode();

        var bobPref = await (await bob.GetAsync("/api/preferences")).Json();
        // Bob never set a theme — must still be "system", unaffected by Alice's pick.
        Assert.Equal("system", Theme(bobPref));
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

    // 27.x — closeout fix: cap the pin list at PinCap (50) and return 400 with a
    // clean message rather than EF's truncation 500 when the CSV column overflows.
    [Fact]
    public async Task Pin_overflow_past_cap_returns_400_not_500()
    {
        var admin = await fx.AdminClientAsync();
        var c = await fx.AuthedClientAsync(await NewUserEmail(), "Pw@123456");
        // Provision PinCap+1 accessible projects (the test user is a TenantAdmin so
        // CanAccessProjectAsync passes for any project in this tenant).
        var ids = new List<int>();
        for (int i = 0; i < BidBuilder.Api.Models.UserPreferences.PinCap; i++)
        {
            var p = (await (await admin.PostAsJsonAsync("/api/projects",
                new { name = $"PinCap-{i}-" + Guid.NewGuid().ToString("N")[..6] })).Json())
                .GetProperty("id").GetInt32();
            ids.Add(p);
        }
        // Pin all PinCap allowed.
        foreach (var pid in ids)
            (await c.PutAsJsonAsync("/api/preferences/pinned", new { projectId = pid, pinned = true })).EnsureSuccessStatusCode();

        // One more should bounce with 400 — NOT a server-side 500.
        var overflow = (await (await admin.PostAsJsonAsync("/api/projects",
            new { name = "PinCap-overflow" })).Json()).GetProperty("id").GetInt32();
        var r = await c.PutAsJsonAsync("/api/preferences/pinned", new { projectId = overflow, pinned = true });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
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
