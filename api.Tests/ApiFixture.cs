using System.Net.Http.Headers;
using System.Net.Http.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Tenancy;
using BidBuilder.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// Boots the real API in-process (WebApplicationFactory) against a throwaway
/// Postgres database created on the dev compose instance (localhost:5433). The
/// app's startup migrates + seeds the DB, so every run gets the known baseline
/// (admin user, RBAC modules, sample project, cost-component types). The database
/// is force-dropped on disposal. Shared across the "api" collection so the host
/// is built once and tests run sequentially against it.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    // Connection bits resolve from the environment first, then the gitignored repo
    // .env, then non-secret defaults. The password is NEVER hard-coded here, so no
    // secret enters source control; CI sets POSTGRES_PASSWORD as a job secret.
    private static readonly Dictionary<string, string> DotEnv = LoadDotEnv();
    private static string Cfg(string key, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(v)) return v;
        return DotEnv.TryGetValue(key, out var dv) && dv.Length > 0 ? dv : fallback;
    }
    private static readonly string Server =
        $"Host={Cfg("BIDBUILDER_TEST_PGHOST", "localhost")};Port={Cfg("BIDBUILDER_TEST_PGPORT", "5433")};" +
        $"Username={Cfg("POSTGRES_USER", "bidbuilder")};Password={Cfg("POSTGRES_PASSWORD", "")}";
    private readonly string _dbName = $"bidbuilder_test_{Guid.NewGuid():N}";

    /// <summary>Best-effort load of the gitignored repo .env (walk up from the test bin dir).</summary>
    private static Dictionary<string, string> LoadDotEnv()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".env"))) dir = dir.Parent;
        if (dir is null) return map;
        foreach (var raw in File.ReadAllLines(Path.Combine(dir.FullName, ".env")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq > 0) map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return map;
    }
    private Factory _factory = null!;

    public string ConnString => $"{Server};Database={_dbName}";

    /// <summary>20.9 — captures outbound webhook deliveries in-memory so tests can
    /// assert dispatch + payload without real HTTP. Replaces the real
    /// <see cref="IWebhookSender"/> for the whole test host (harmless to non-webhook tests).</summary>
    public static readonly CapturingWebhookSender Webhooks = new();

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IWebhookSender>();
                s.AddSingleton<IWebhookSender>(Webhooks);
            });
        }
    }

    public Task InitializeAsync()
    {
        // Must be set BEFORE the host builds: the app reads ConnectionStrings:Postgres
        // during CreateBuilder, before any WebApplicationFactory config callback runs.
        // The double-underscore form maps to ConnectionStrings:Postgres in .NET config.
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", ConnString);
        // The host runs as Production (skips Swagger) → it now requires a strong, non-default
        // signing key, so supply one. This also exercises the production secret guards.
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "test-signing-key-not-a-default-0123456789abcdef");
        // Production CORS refuses to boot without a real (non-localhost) origin.
        Environment.SetEnvironmentVariable("AllowedOrigins", "https://app.bidbuilder.test");
        // Production no longer seeds a default admin or demo content implicitly; the tests
        // need the known baseline, so opt in explicitly (mirrors a real first-boot config).
        Environment.SetEnvironmentVariable("Seed__AdminPassword", "Admin@12345");
        Environment.SetEnvironmentVariable("Seed__DemoData", "true");
        // Seed a platform SuperAdmin so the platform/tenant-management tests have an operator.
        Environment.SetEnvironmentVariable("Seed__SuperAdminPassword", "Super@12345");
        // Disable Hangfire in the test host so the cascade runs INLINE inside the request
        // that triggered it. Tests assert the cascaded effect (assembly ComputedRate,
        // estimate roll-ups) immediately after a resource mutation; if the cascade went to
        // a Hangfire queue, the next assertion would race the background worker. The
        // production Hangfire path is exercised separately by a smoke flow.
        Environment.SetEnvironmentVariable("Hangfire__Enabled", "false");
        _factory = new Factory();
        _ = _factory.Services;   // force host build → runs DbInitializer (migrate + seed)
        return Task.CompletedTask;
    }

    /// <summary>A client carrying the default tenant header (unauthenticated).</summary>
    public HttpClient Client() => ClientForTenant("default");

    /// <summary>An unauthenticated client carrying an arbitrary X-Tenant-Id header.</summary>
    public HttpClient ClientForTenant(string slug)
    {
        var c = NewClient();
        c.DefaultRequestHeaders.Add("X-Tenant-Id", slug);
        return c;
    }

    /// <summary>An unauthenticated client with NO tenant header — used to exercise
    /// host-based (custom-domain) tenant resolution (20.11).</summary>
    public HttpClient AnonymousClient() => NewClient();

    /// <summary>An unauthenticated client that does NOT auto-follow redirects, so a
    /// test can inspect a 302 Location (e.g. the SAML ACS handing back a session, 20.8b).
    /// Carries no tenant header — SSO endpoints take the tenant from the URL slug.</summary>
    public HttpClient NoRedirectClient()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Forwarded-For", $"203.0.113.{Guid.NewGuid().GetHashCode() & 0xFF}.{Environment.CurrentManagedThreadId}");
        return c;
    }

    /// <summary>Create a client with a UNIQUE forwarded client IP so the login
    /// rate-limiter (partitioned on X-Forwarded-For) buckets each test independently —
    /// one test's logins never deplete another's allowance.</summary>
    private HttpClient NewClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forwarded-For", $"203.0.113.{Guid.NewGuid().GetHashCode() & 0xFF}.{Environment.CurrentManagedThreadId}");
        return c;
    }

    /// <summary>An unauthenticated client (no Bearer token). Used to verify 401 responses.</summary>
    public HttpClient AnonClient() => NewClient();

    /// <summary>A client authenticated as the seeded tenant admin.</summary>
    public Task<HttpClient> AdminClientAsync() => AuthedClientAsync("admin@bidbuilder.local", "Admin@12345");

    /// <summary>Alias for <see cref="SecondTenantAdminClientAsync"/> used by isolation tests.</summary>
    public Task<HttpClient> SecondAdminClientAsync() => SecondTenantAdminClientAsync();

    /// <summary>
    /// Returns a DI scope for the default tenant. Useful for service-level tests that need
    /// to instantiate services with a real DB + tenant context. The caller is responsible for
    /// disposing the scope (use <c>await using</c>).
    /// </summary>
    public async ValueTask<IServiceScope> CreateScope(string tenantSlug = "default")
    {
        var scope = _factory.Services.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == tenantSlug);
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant.Id, tenant.Slug);
        return scope;
    }

    /// <summary>Log in as an arbitrary existing user (default tenant unless overridden)
    /// and return a Bearer-authenticated client.</summary>
    public async Task<HttpClient> AuthedClientAsync(string email, string password, string tenantSlug = "default")
    {
        var c = NewClient();
        c.DefaultRequestHeaders.Add("X-Tenant-Id", tenantSlug);
        var resp = await c.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return c;
    }

    /// <summary>
    /// Seed a SECOND tenant (idempotently) with its own TenantAdmin and return a client
    /// logged in as that admin. There's no tenant-provisioning endpoint, so this writes
    /// the Tenant + admin User directly via a DI scope. Tenant/User aren't IHasTenant, so
    /// the SaveChanges auto-stamp doesn't fire; the tenant context is resolved before the
    /// User insert so its TenantId is set explicitly. Used to prove cross-tenant isolation.
    /// </summary>
    public async Task<HttpClient> SecondTenantAdminClientAsync(
        string slug = "tenant2", string email = "admin@tenant2.local", string password = "Admin@12345")
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Tenants.FirstOrDefaultAsync(x => x.Slug == slug) is null)
            {
                var t = new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = "Second Tenant", DefaultLocale = "en" };
                db.Tenants.Add(t);
                await db.SaveChangesAsync();
                // Resolve the tenant so this scope's context targets it for any further writes.
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(t.Id, t.Slug);
                db.Users.Add(new User
                {
                    TenantId = t.Id, Email = email, Name = "T2 Admin",
                    Role = UserRole.TenantAdmin, IsActive = true,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                });
                await db.SaveChangesAsync();
            }
        }
        return await AuthedClientAsync(email, password, slug);
    }

    /// <summary>
    /// A client authenticated as the seeded platform SuperAdmin. SuperAdmins have no
    /// tenant, so this sends NO X-Tenant-Id and uses the dedicated platform-login path.
    /// </summary>
    public async Task<HttpClient> PlatformAdminClientAsync(
        string email = "superadmin@bidbuilder.local", string password = "Super@12345")
    {
        var c = NewClient();   // deliberately no X-Tenant-Id header
        var resp = await c.PostAsJsonAsync("/api/auth/platform-login", new { email, password });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return c;
    }

    /// <summary>Run an action against a fresh DI-scoped <see cref="AppDbContext"/> with the
    /// given tenant resolved. Lets hardening tests probe DB-level constraints (e.g. the tenant
    /// foreign keys) directly, below the HTTP/application layer.</summary>
    public async Task WithTenantDbAsync(string slug, Func<AppDbContext, Tenant, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == slug);
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant.Id, tenant.Slug);
        await action(db, tenant);
    }

    private sealed record LoginResponse(string Token);

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await using var conn = new NpgsqlConnection($"{Server};Database=postgres");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE);";
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture> { }
