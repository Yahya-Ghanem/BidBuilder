using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────
/// <summary>An API key as shown in the management list — never includes the secret,
/// which is shown exactly once at creation. (22.2 adds scopes, rate limit + live usage.)</summary>
public record ApiKeyDto(
    int Id, string Name, string Prefix, DateTime CreatedAt,
    DateTime? LastUsedAt, DateTime? ExpiresAt, bool Revoked,
    /// <summary>22.2 — granted scopes, e.g. ["read","write"].</summary>
    string[] Scopes,
    /// <summary>22.2 — per-minute cap, or null when unlimited.</summary>
    int? RateLimitPerMinute,
    /// <summary>22.2 — requests counted against this key in the current minute (live, in-process).</summary>
    int UsageThisMinute);

/// <summary>22.2 — Scopes default to full access (["read","write"]) when omitted, so the
/// existing create shape keeps working. RateLimitPerMinute null = unlimited.</summary>
public record CreateApiKeyInput(string? Name, int? ExpiresInDays, string[]? Scopes, int? RateLimitPerMinute);

/// <summary>The create response — the only time the raw <see cref="Secret"/> is returned.</summary>
public record ApiKeyCreatedDto(ApiKeyDto Key, string Secret);

/// <summary>
/// 21.2 — Tenant-admin management of programmatic API keys (`/api/admin/api-keys`).
/// A key authenticates a headless caller AS its creating admin (via the
/// <c>X-Api-Key</c> header), inheriting that user's permissions. The secret is shown
/// once on creation and only its hash is stored; listing returns the prefix only.
/// </summary>
public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/admin/api-keys").RequireAuthorization();

        // List the tenant's keys, newest first (never the secret). Includes each key's
        // live request count for the current minute (from the in-process limiter).
        grp.MapGet("/", async (ClaimsPrincipal me, AppDbContext db, ApiKeyRateLimiter limiter) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var rows = await db.ApiKeys.OrderByDescending(k => k.Id).ToListAsync();
            var now = DateTime.UtcNow;
            return Results.Ok(rows.Select(k => ToDto(k, limiter.CurrentCount(k.Id, now))).ToList());
        });

        // Mint a new key — returns the secret ONCE.
        grp.MapPost("/", async (CreateApiKeyInput input, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var name = input.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return Bad("Name is required.");
            if (name!.Length > 120) return Bad("Name is too long (max 120).");
            if (input.ExpiresInDays is { } d && d <= 0) return Bad("Expiry, if set, must be a positive number of days.");

            // Validate + normalize scopes. Omitted/empty → full access (back-compat with 21.2).
            if (!TryNormalizeScopes(input.Scopes, out var scopesCsv, out var scopeErr)) return Bad(scopeErr!);
            if (input.RateLimitPerMinute is { } rl && rl <= 0)
                return Bad("Rate limit, if set, must be a positive number of requests per minute.");

            var (secret, prefix) = ApiKeyTokens.Generate();
            var key = new ApiKey
            {
                UserId             = me.Id(),
                Name               = name,
                Prefix             = prefix,
                KeyHash            = ApiKeyTokens.Hash(secret),
                Scopes             = scopesCsv,
                RateLimitPerMinute = input.RateLimitPerMinute,
                ExpiresAt          = input.ExpiresInDays is { } days ? DateTime.UtcNow.AddDays(days) : null,
            };
            db.ApiKeys.Add(key);                       // TenantId auto-stamped on save
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "api-key.create", "ApiKey", key.Id.ToString(),
                $"{name} ({prefix}…) scopes={scopesCsv}{(key.RateLimitPerMinute is { } r ? $" rate={r}/min" : "")}");

            return Results.Created($"/api/admin/api-keys/{key.Id}", new ApiKeyCreatedDto(ToDto(key), secret));
        });

        // Revoke a key (keeps the row for the audit trail; the key stops authenticating immediately).
        grp.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id);
            if (key is null) return Results.NotFound(new { error = "API key not found" });
            if (key.RevokedAt is null)
            {
                key.RevokedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                await audit.LogAsync(me, "api-key.revoke", "ApiKey", key.Id.ToString(), $"{key.Name} ({key.Prefix}…)");
            }
            return Results.Ok(ToDto(key));
        });
    }

    private static ApiKeyDto ToDto(ApiKey k, int usageThisMinute = 0) => new(
        k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.ExpiresAt, k.RevokedAt is not null,
        ScopeArray(k.Scopes), k.RateLimitPerMinute, usageThisMinute);

    /// <summary>The only scopes a key may carry. Kept binary for v22.2 (read = safe methods,
    /// write = mutations); per-resource scopes can extend this later.</summary>
    private static readonly string[] AllowedScopes = { "read", "write" };

    private static string[] ScopeArray(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Validate + normalize the requested scopes into a canonical CSV. Null/empty →
    /// "read,write" (full access). Rejects anything outside <see cref="AllowedScopes"/>.</summary>
    private static bool TryNormalizeScopes(string[]? requested, out string csv, out string? error)
    {
        error = null;
        var scopes = (requested ?? Array.Empty<string>())
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
        if (scopes.Count == 0) { csv = "read,write"; return true; }
        var invalid = scopes.Where(s => !AllowedScopes.Contains(s)).ToList();
        if (invalid.Count > 0)
        {
            csv = "";
            error = $"Unknown scope(s): {string.Join(", ", invalid)}. Allowed: {string.Join(", ", AllowedScopes)}.";
            return false;
        }
        // Canonical order so the stored value is stable regardless of input order.
        csv = string.Join(",", AllowedScopes.Where(scopes.Contains));
        return true;
    }

    private static IResult Bad(string msg) => Results.BadRequest(new { error = msg });
    private static IResult Forbid() => Results.Json(new { error = "Only a tenant admin can manage API keys." }, statusCode: 403);
}
