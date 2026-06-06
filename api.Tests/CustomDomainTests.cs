using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Endpoints;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.11 coverage: per-tenant custom domain. The contract:
///
///   1. A tenant admin can register a vanity host; it round-trips on GET /api/settings.
///   2. The middleware resolves a tokenless, header-less request to that tenant by
///      matching the request Host — so a workspace is reachable at bids.acme.com
///      without passing X-Tenant-Id (proven via an anonymous login).
///   3. A second tenant cannot claim a domain already in use (409).
///   4. Garbage hostnames are rejected (400); clearing it works.
///   5. Non-admins cannot change it (403).
/// </summary>
[Collection("api")]
public class CustomDomainTests(ApiFixture fx)
{
    private static async Task SetDomain(HttpClient admin, string? domain, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var r = await admin.PutAsJsonAsync("/api/settings/custom-domain", new { domain });
        Assert.Equal(expect, r.StatusCode);
    }

    [Fact]
    public async Task Admin_registers_domain_and_it_resolves_an_anonymous_request()
    {
        var admin = await fx.AdminClientAsync();
        var domain = $"bids-{System.Guid.NewGuid():N}".Substring(0, 14) + ".acme.test";
        try
        {
            await SetDomain(admin, domain);

            // It round-trips on settings.
            var settings = await admin.GetFromJsonAsync<JsonElement>("/api/settings");
            Assert.Equal(domain, settings.GetProperty("customDomain").GetString());

            // A tokenless, tenant-header-less request whose Host matches the domain
            // resolves the tenant — the anonymous login succeeds.
            var anon = fx.AnonymousClient();
            var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { email = "admin@bidbuilder.local", password = "Admin@12345" }),
            };
            login.Headers.Host = domain;
            var resp = await anon.SendAsync(login);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Control: same request with NO host match and NO tenant header → 401 in Production.
            var noHost = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { email = "admin@bidbuilder.local", password = "Admin@12345" }),
            };
            var resp2 = await anon.SendAsync(noHost);
            Assert.Equal(HttpStatusCode.Unauthorized, resp2.StatusCode);
        }
        finally
        {
            await SetDomain(admin, null);   // clear so the unique index doesn't leak across tests
        }
    }

    [Fact]
    public async Task A_domain_in_use_by_another_tenant_is_rejected()
    {
        var admin = await fx.AdminClientAsync();
        var other = await fx.SecondTenantAdminClientAsync();
        var domain = $"shared-{System.Guid.NewGuid():N}".Substring(0, 14) + ".example.test";
        try
        {
            await SetDomain(admin, domain);
            // Second tenant tries to claim the same host → 409.
            var r = await other.PutAsJsonAsync("/api/settings/custom-domain", new { domain });
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        }
        finally
        {
            await SetDomain(admin, null);
        }
    }

    [Fact]
    public async Task Invalid_hostname_is_rejected()
    {
        var admin = await fx.AdminClientAsync();
        await SetDomain(admin, "not a domain", HttpStatusCode.BadRequest);
        await SetDomain(admin, "localhost", HttpStatusCode.BadRequest);   // single label
    }

    [Fact]
    public async Task Non_admin_cannot_change_the_domain()
    {
        // A plain unauthenticated (default-tenant) client is not an admin → 403/401.
        var anon = fx.Client();
        var r = await anon.PutAsJsonAsync("/api/settings/custom-domain", new { domain = "x.example.test" });
        Assert.True(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public void Domain_validator_accepts_real_hosts_and_rejects_junk()
    {
        Assert.True(SettingsEndpoints.IsValidDomain("bids.acme.com"));
        Assert.True(SettingsEndpoints.IsValidDomain("a.b.c.example.co"));
        Assert.False(SettingsEndpoints.IsValidDomain("localhost"));
        Assert.False(SettingsEndpoints.IsValidDomain("-bad.example.com"));
        Assert.False(SettingsEndpoints.IsValidDomain("bad-.example.com"));
        Assert.False(SettingsEndpoints.IsValidDomain("space here.com"));
        Assert.False(SettingsEndpoints.IsValidDomain("trailingtld.123"));
    }
}
