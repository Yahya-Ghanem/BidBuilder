using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Production");
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
        _factory = new Factory();
        _ = _factory.Services;   // force host build → runs DbInitializer (migrate + seed)
        return Task.CompletedTask;
    }

    /// <summary>A client carrying the default tenant header (unauthenticated).</summary>
    public HttpClient Client()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Tenant-Id", "default");
        return c;
    }

    /// <summary>A client authenticated as the seeded tenant admin.</summary>
    public async Task<HttpClient> AdminClientAsync()
    {
        var c = Client();
        var resp = await c.PostAsJsonAsync("/api/auth/login", new { email = "admin@bidbuilder.local", password = "Admin@12345" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return c;
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
