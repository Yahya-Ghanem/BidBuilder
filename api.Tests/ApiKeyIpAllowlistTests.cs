using System.Net;
using System.Net.Http.Json;
using BidBuilder.Api.Auth;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 23.2 — API-key IP allowlist. A key may carry a comma-separated list of CIDRs (IPv4 or IPv6);
/// a request from outside any of them is rejected 403 by <see cref="Auth.ApiKeyGuard"/> with an
/// "IP not allowed" reason. Null/empty allowlist = unrestricted (back-compat with 22.2 keys).
///
/// The test server reports the connection's <see cref="System.Net.IPAddress"/> as IPv6 loopback
/// (<c>::1</c>) — so "loopback" allowlists in these tests use both forms to be portable.
/// </summary>
[Collection("api")]
public class ApiKeyIpAllowlistTests(ApiFixture fx)
{
    static async Task<string> CreateKeyAsync(HttpClient admin, string name, string[]? ipAllowlist = null)
    {
        var resp = await admin.PostAsJsonAsync("/api/admin/api-keys/",
            new { name, scopes = new[] { "read", "write" }, ipAllowlist });
        resp.EnsureSuccessStatusCode();
        return (await resp.Json()).GetProperty("secret").GetString()!;
    }

    HttpClient KeyClient(string secret)
    {
        var c = fx.AnonClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return c;
    }

    [Fact]
    public async Task Empty_allowlist_permits_any_caller()
    {
        var admin = await fx.AdminClientAsync();
        var c = KeyClient(await CreateKeyAsync(admin, "anywhere"));   // no allowlist = unrestricted

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Allowlist_matching_loopback_permits_the_call()
    {
        var admin = await fx.AdminClientAsync();
        // Cover both v4 + v6 loopback families; TestServer may stamp either.
        var c = KeyClient(await CreateKeyAsync(admin, "loopback",
            ipAllowlist: new[] { "127.0.0.1/32", "::1/128" }));

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Allowlist_that_excludes_the_caller_returns_403_with_distinct_message()
    {
        var admin = await fx.AdminClientAsync();
        // 203.0.113.0/24 is the IANA TEST-NET-3 range — guaranteed not the loopback the
        // TestServer reports, so the guard must reject this call.
        var c = KeyClient(await CreateKeyAsync(admin, "offsite", ipAllowlist: new[] { "203.0.113.0/24" }));

        var resp = await c.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        // Distinct reason: "IP allowlist" rather than the scope check's "scope" message.
        Assert.Contains("IP allowlist", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_cidr_is_rejected_at_create()
    {
        var admin = await fx.AdminClientAsync();
        var resp = await admin.PostAsJsonAsync("/api/admin/api-keys/", new
        {
            name = "bad-cidr",
            scopes = new[] { "read" },
            ipAllowlist = new[] { "not-an-ip-or-cidr/24" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Allowlist_can_be_updated_via_put_and_takes_effect_for_the_next_request()
    {
        var admin = await fx.AdminClientAsync();
        // Start permissive — the key works.
        var resp = await admin.PostAsJsonAsync("/api/admin/api-keys/",
            new { name = "rotating-allowlist", scopes = new[] { "read", "write" } });
        resp.EnsureSuccessStatusCode();
        var created = await resp.Json();
        var keyId  = created.GetProperty("key").GetProperty("id").GetInt32();
        var secret = created.GetProperty("secret").GetString()!;
        var c = KeyClient(secret);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/projects")).StatusCode);

        // Lock down to a non-loopback range → call now rejected.
        var put = await admin.PutAsJsonAsync($"/api/admin/api-keys/{keyId}/allowlist",
            new { ipAllowlist = new[] { "203.0.113.0/24" } });
        put.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await KeyClient(secret).GetAsync("/api/projects")).StatusCode);

        // Clear the allowlist → unrestricted again.
        (await admin.PutAsJsonAsync($"/api/admin/api-keys/{keyId}/allowlist",
            new { ipAllowlist = Array.Empty<string>() })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await KeyClient(secret).GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Allowlist_round_trips_in_the_list_dto()
    {
        var admin = await fx.AdminClientAsync();
        var name = $"reported-{Guid.NewGuid():N}";
        await CreateKeyAsync(admin, name, ipAllowlist: new[] { "10.0.0.0/8", "::1/128" });

        var list = await (await admin.GetAsync("/api/admin/api-keys/")).Json();
        var row = list.EnumerateArray().First(k => k.GetProperty("name").GetString() == name);
        var allow = row.GetProperty("ipAllowlist").EnumerateArray().Select(s => s.GetString()).ToList();

        Assert.Contains("10.0.0.0/8", allow);
        Assert.Contains("::1/128", allow);
    }
}

/// <summary>23.2 — Pure unit tests for the CIDR matcher (no host, no DB) so the IPv4 /
/// IPv6 / bare-host / mapped paths are deterministic and fast.</summary>
public class CidrMatcherUnitTests
{
    [Fact]
    public void Empty_allowlist_means_any_ip()
    {
        Assert.True(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("8.8.8.8"), null));
        Assert.True(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("8.8.8.8"), ""));
    }

    [Fact]
    public void Ipv4_cidr_matches_inside_and_rejects_outside()
    {
        Assert.True(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("10.1.2.3"), "10.0.0.0/8"));
        Assert.False(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("11.0.0.1"), "10.0.0.0/8"));
    }

    [Fact]
    public void Bare_ipv4_is_treated_as_host_route()
    {
        Assert.True(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("1.2.3.4"), "1.2.3.4"));
        Assert.False(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("1.2.3.5"), "1.2.3.4"));
    }

    [Fact]
    public void Ipv6_cidr_matches_inside_and_rejects_outside()
    {
        Assert.True(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("2001:db8::1"), "2001:db8::/32"));
        Assert.False(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("2002:db8::1"), "2001:db8::/32"));
    }

    [Fact]
    public void Mapped_ipv6_is_normalized_to_ipv4_for_comparison()
    {
        // ::ffff:127.0.0.1 must match a v4 CIDR — Kestrel stamps mapped form on dual-stack sockets.
        Assert.True(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("::ffff:127.0.0.1"), "127.0.0.0/8"));
    }

    [Fact]
    public void Multiple_entries_are_or_matched()
    {
        Assert.True(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("10.0.0.5"), "192.168.0.0/16, 10.0.0.0/8"));
    }

    [Fact]
    public void Malformed_entries_are_skipped_not_matched()
    {
        // A garbage entry mustn't "match-all" — false unless a real CIDR catches it.
        Assert.False(CidrMatcher.IsAllowed(System.Net.IPAddress.Parse("8.8.8.8"), "garbage/cidr"));
    }

    [Fact]
    public void TryParse_validates_and_canonicalizes()
    {
        Assert.True(CidrMatcher.TryParse("10.0.0.0/8", out var c1));
        Assert.Equal("10.0.0.0/8", c1);

        Assert.True(CidrMatcher.TryParse("1.2.3.4", out var c2));
        Assert.Equal("1.2.3.4/32", c2);

        Assert.False(CidrMatcher.TryParse("1.2.3.4/33", out _));      // bad prefix
        Assert.False(CidrMatcher.TryParse("not-an-ip/24", out _));    // bad address
    }
}
