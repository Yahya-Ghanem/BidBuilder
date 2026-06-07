using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Auth;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 22.2 — API-key scopes + per-key rate limits. A key's scopes gate which HTTP methods it
/// may use (read = safe methods, write = mutations) on top of the owner's RBAC; an optional
/// per-minute limit caps throughput (429). Enforced by ApiKeyGuard for X-Api-Key requests only.
/// </summary>
[Collection("api")]
public class ApiKeyScopeRateLimitTests(ApiFixture fx)
{
    async Task<string> CreateKeyAsync(HttpClient admin, string name, string[]? scopes = null, int? rateLimitPerMinute = null)
    {
        var resp = await admin.PostAsJsonAsync("/api/admin/api-keys/", new { name, scopes, rateLimitPerMinute });
        resp.EnsureSuccessStatusCode();
        return (await resp.Json()).GetProperty("secret").GetString()!;
    }

    HttpClient KeyClient(string secret)
    {
        var c = fx.AnonClient();                 // no bearer, no X-Tenant-Id — the key authenticates
        c.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return c;
    }

    static Task<HttpResponseMessage> WriteAsync(HttpClient c) =>
        c.PostAsJsonAsync("/api/activities", new { name = $"ScopeTest {Guid.NewGuid():N}", sortOrder = 0, isActive = true });

    [Fact]
    public async Task Read_only_key_can_read_but_not_write()
    {
        var admin = await fx.AdminClientAsync();
        var c = KeyClient(await CreateKeyAsync(admin, "read-only", scopes: new[] { "read" }));

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await WriteAsync(c)).StatusCode);
    }

    [Fact]
    public async Task Write_scope_permits_mutations()
    {
        var admin = await fx.AdminClientAsync();
        var c = KeyClient(await CreateKeyAsync(admin, "writer", scopes: new[] { "read", "write" }));

        var resp = await WriteAsync(c);
        Assert.True(resp.IsSuccessStatusCode, $"expected success for a write-scoped key, got {resp.StatusCode}");
    }

    [Fact]
    public async Task Write_only_key_cannot_read()
    {
        var admin = await fx.AdminClientAsync();
        var c = KeyClient(await CreateKeyAsync(admin, "write-only", scopes: new[] { "write" }));

        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Default_scopes_grant_full_access_back_compat()
    {
        var admin = await fx.AdminClientAsync();
        // Omit scopes entirely — must behave like a pre-22.2 key (full read+write).
        var c = KeyClient(await CreateKeyAsync(admin, "legacy-style"));

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/projects")).StatusCode);
        Assert.True((await WriteAsync(c)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Invalid_scope_is_rejected_at_create()
    {
        var admin = await fx.AdminClientAsync();
        var resp = await admin.PostAsJsonAsync("/api/admin/api-keys/", new { name = "bad", scopes = new[] { "admin" } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_returns_429_after_the_budget()
    {
        var admin = await fx.AdminClientAsync();
        var c = KeyClient(await CreateKeyAsync(admin, "throttled", scopes: new[] { "read" }, rateLimitPerMinute: 3));

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/projects")).StatusCode);

        var blocked = await c.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Unlimited_key_never_throttles()
    {
        var admin = await fx.AdminClientAsync();
        var c = KeyClient(await CreateKeyAsync(admin, "unlimited", scopes: new[] { "read" }));

        for (var i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Usage_and_scopes_are_reported_in_the_list()
    {
        var admin = await fx.AdminClientAsync();
        var name = $"usage-{Guid.NewGuid():N}";
        var c = KeyClient(await CreateKeyAsync(admin, name, scopes: new[] { "read" }, rateLimitPerMinute: 100));

        for (var i = 0; i < 4; i++) await c.GetAsync("/api/projects");

        var list = await (await admin.GetAsync("/api/admin/api-keys/")).Json();
        var row = list.EnumerateArray().First(k => k.GetProperty("name").GetString() == name);

        Assert.True(row.GetProperty("usageThisMinute").GetInt32() >= 4);
        Assert.Equal(100, row.GetProperty("rateLimitPerMinute").GetInt32());
        Assert.Contains("read", row.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.DoesNotContain("write", row.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
    }
}

/// <summary>22.2 — Pure unit tests for the fixed-window <see cref="ApiKeyRateLimiter"/>
/// (no host, time injected) so the window-reset + unlimited paths are deterministic.</summary>
public class ApiKeyRateLimiterUnitTests
{
    [Fact]
    public void Window_blocks_over_budget_then_resets()
    {
        var limiter = new ApiKeyRateLimiter();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(limiter.Check(1, 2, t0).Allowed);            // 1st
        Assert.True(limiter.Check(1, 2, t0).Allowed);            // 2nd
        Assert.False(limiter.Check(1, 2, t0).Allowed);           // 3rd over budget
        Assert.Equal(2, limiter.CurrentCount(1, t0));

        Assert.True(limiter.Check(1, 2, t0.AddSeconds(61)).Allowed);  // new window
        Assert.Equal(1, limiter.CurrentCount(1, t0.AddSeconds(61)));
    }

    [Fact]
    public void Unlimited_key_is_always_allowed_and_uncounted()
    {
        var limiter = new ApiKeyRateLimiter();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 100; i++) Assert.True(limiter.Check(7, null, t0).Allowed);
        Assert.Equal(0, limiter.CurrentCount(7, t0));
    }
}
