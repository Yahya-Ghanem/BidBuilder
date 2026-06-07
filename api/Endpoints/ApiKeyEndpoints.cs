using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────
/// <summary>An API key as shown in the management list — never includes the secret,
/// which is shown exactly once at creation.</summary>
public record ApiKeyDto(
    int Id, string Name, string Prefix, DateTime CreatedAt,
    DateTime? LastUsedAt, DateTime? ExpiresAt, bool Revoked);

public record CreateApiKeyInput(string? Name, int? ExpiresInDays);

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

        // List the tenant's keys, newest first (never the secret).
        grp.MapGet("/", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var rows = await db.ApiKeys.OrderByDescending(k => k.Id).ToListAsync();
            return Results.Ok(rows.Select(ToDto).ToList());
        });

        // Mint a new key — returns the secret ONCE.
        grp.MapPost("/", async (CreateApiKeyInput input, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var name = input.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return Bad("Name is required.");
            if (name!.Length > 120) return Bad("Name is too long (max 120).");
            if (input.ExpiresInDays is { } d && d <= 0) return Bad("Expiry, if set, must be a positive number of days.");

            var (secret, prefix) = ApiKeyTokens.Generate();
            var key = new ApiKey
            {
                UserId    = me.Id(),
                Name      = name,
                Prefix    = prefix,
                KeyHash   = ApiKeyTokens.Hash(secret),
                ExpiresAt = input.ExpiresInDays is { } days ? DateTime.UtcNow.AddDays(days) : null,
            };
            db.ApiKeys.Add(key);                       // TenantId auto-stamped on save
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "api-key.create", "ApiKey", key.Id.ToString(), $"{name} ({prefix}…)");

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

    private static ApiKeyDto ToDto(ApiKey k) => new(
        k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.ExpiresAt, k.RevokedAt is not null);

    private static IResult Bad(string msg) => Results.BadRequest(new { error = msg });
    private static IResult Forbid() => Results.Json(new { error = "Only a tenant admin can manage API keys." }, statusCode: 403);
}
