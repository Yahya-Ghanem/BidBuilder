using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 21.2 — Programmatic API keys. A key authenticates a headless caller via the
/// <c>X-Api-Key</c> header, acting as its creating admin (no JWT, no tenant header —
/// the tenant resolves from the key).
///
/// Coverage:
///   • create returns the secret once; the list never exposes it.
///   • a key authenticates a real request (tenant resolved from the key alone).
///   • garbage / revoked / expired keys are all rejected (401).
///   • a deactivated owner disables the key.
///   • only admins can manage keys.
/// </summary>
[Collection("api")]
public class ApiKeyTests(ApiFixture fx)
{
    async Task<(string secret, int id, JsonElement key)> CreateKeyAsync(HttpClient admin, string name, int? expiresInDays = null)
    {
        var resp = await admin.PostAsJsonAsync("/api/admin/api-keys/", new { name, expiresInDays });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Json();
        var key = body.GetProperty("key");
        return (body.GetProperty("secret").GetString()!, key.GetProperty("id").GetInt32(), key);
    }

    HttpClient KeyClient(string secret)
    {
        var c = fx.AnonClient();                 // no bearer, no X-Tenant-Id
        c.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return c;
    }

    [Fact]
    public async Task Create_returns_secret_once_and_list_never_does()
    {
        var admin = await fx.AdminClientAsync();
        var (secret, id, key) = await CreateKeyAsync(admin, "CI pipeline");

        Assert.StartsWith("bbk_", secret);
        Assert.StartsWith("bbk_", key.GetProperty("prefix").GetString());
        Assert.False(key.TryGetProperty("secret", out _));    // the DTO has no secret field

        // The list returns the prefix but never the full secret.
        var listRaw = await (await admin.GetAsync("/api/admin/api-keys/")).Content.ReadAsStringAsync();
        Assert.Contains($"\"id\":{id}", listRaw.Replace(" ", ""));
        Assert.DoesNotContain(secret, listRaw);
    }

    [Fact]
    public async Task Key_authenticates_a_request_via_the_header()
    {
        var admin = await fx.AdminClientAsync();
        var (secret, _, _) = await CreateKeyAsync(admin, "reader");

        // No bearer token, no tenant header — the key alone authenticates AND resolves the tenant.
        var resp = await KeyClient(secret).GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var projects = await resp.Json();
        Assert.True(projects.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Garbage_key_is_rejected()
    {
        var resp = await KeyClient("bbk_not-a-real-key").GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Revoked_key_is_rejected()
    {
        var admin = await fx.AdminClientAsync();
        var (secret, id, _) = await CreateKeyAsync(admin, "to-revoke");

        Assert.Equal(HttpStatusCode.OK, (await KeyClient(secret).GetAsync("/api/projects")).StatusCode);
        (await admin.DeleteAsync($"/api/admin/api-keys/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await KeyClient(secret).GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Expired_key_is_rejected()
    {
        var admin = await fx.AdminClientAsync();
        var (secret, id, _) = await CreateKeyAsync(admin, "short-lived", expiresInDays: 30);

        // Backdate the expiry directly (the API only mints future expiries).
        using (var scope = await fx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var k = await db.ApiKeys.FirstAsync(x => x.Id == id);
            k.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await KeyClient(secret).GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Deactivated_owner_disables_the_key()
    {
        var admin = await fx.AdminClientAsync();
        var ownerEmail = $"apikey.owner.{Guid.NewGuid():N}@bidbuilder.local";
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Key Owner", email = ownerEmail, password = "Pw@123456", role = "TenantAdmin", groupIds = Array.Empty<int>() })).Json();
        var owner = await fx.AuthedClientAsync(ownerEmail, "Pw@123456");

        var (secret, _, _) = await CreateKeyAsync(owner, "owned-by-second-admin");
        Assert.Equal(HttpStatusCode.OK, (await KeyClient(secret).GetAsync("/api/projects")).StatusCode);

        // Deactivate the owner → the key must stop authenticating.
        using (var scope = await fx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var u = await db.Users.FirstAsync(x => x.Email == ownerEmail);
            u.IsActive = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await KeyClient(secret).GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Non_admin_cannot_manage_keys()
    {
        var admin = await fx.AdminClientAsync();
        var email = $"apikey.plain.{Guid.NewGuid():N}@bidbuilder.local";
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Plain", email, password = "Pw@123456", role = "TenantUser", groupIds = Array.Empty<int>() })).Json();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/api-keys/")).StatusCode);
        var create = await user.PostAsJsonAsync("/api/admin/api-keys/", new { name = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }
}
