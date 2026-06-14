using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// Plannix W6b — GET /api/auth/me returns the authenticated caller's tenant slug
/// (claims/ITenantContext-derived, no DB hit). A sibling app uses it to confirm a
/// bbk_ API key belongs to the expected tenant when testing a connection. Must be
/// reachable by a read-scoped API key (GET ⇒ "read" scope) as well as a JWT, and
/// must reject anonymous callers.
/// </summary>
[Collection("api")]
public class AuthIdentityTests(ApiFixture fx)
{
    async Task<string> CreateReadKeyAsync(HttpClient admin)
    {
        var resp = await admin.PostAsJsonAsync("/api/admin/api-keys/",
            new { name = $"me-probe-{Guid.NewGuid():N}", scopes = new[] { "read" } });
        resp.EnsureSuccessStatusCode();
        return (await resp.Json()).GetProperty("secret").GetString()!;
    }

    HttpClient KeyClient(string secret)
    {
        var c = fx.AnonClient();                 // no bearer, no X-Tenant-Id — the key authenticates
        c.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return c;
    }

    [Fact]
    public async Task Read_scoped_api_key_gets_its_own_tenant_slug()
    {
        var admin = await fx.AdminClientAsync();
        var secret = await CreateReadKeyAsync(admin);
        Assert.StartsWith("bbk_", secret);

        var c = KeyClient(secret);
        var resp = await c.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Json();
        Assert.Equal("default", body.GetProperty("tenantSlug").GetString());
    }

    [Fact]
    public async Task Jwt_caller_gets_its_own_tenant_slug()
    {
        var admin = await fx.AdminClientAsync();
        var resp = await admin.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Json();
        Assert.Equal("default", body.GetProperty("tenantSlug").GetString());
    }

    [Fact]
    public async Task Write_only_api_key_is_forbidden()
    {
        // A GET requires "read" scope. A write-only key must be refused (403), even
        // though it authenticates fine — scope, not identity, is what's missing.
        var admin = await fx.AdminClientAsync();
        var resp = await admin.PostAsJsonAsync("/api/admin/api-keys/",
            new { name = $"me-probe-{Guid.NewGuid():N}", scopes = new[] { "write" } });
        resp.EnsureSuccessStatusCode();
        var secret = (await resp.Json()).GetProperty("secret").GetString()!;

        var c = KeyClient(secret);
        var me = await c.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
    }

    [Fact]
    public async Task Anonymous_caller_is_unauthorized()
    {
        var c = fx.Client();   // tenant header but no credentials
        var resp = await c.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
