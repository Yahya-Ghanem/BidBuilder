using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

public record FeatureFlagDto(
    int Id,
    string Key,
    string Description,
    bool Enabled,
    int RolloutPercentage,
    Dictionary<string, bool> Overrides,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record FeatureFlagInput(
    string? Description,
    bool? Enabled,
    int? RolloutPercentage,
    Dictionary<string, bool>? Overrides);

/// <summary>
/// 29.B.1 — Endpoints over the feature-flag catalogue.
/// </summary>
/// <remarks>
/// Two surfaces:
/// <list type="bullet">
///   <item><description>Admin: <c>GET/PUT /api/feature-flags</c> — SuperAdmin
///     only; reading the raw catalogue with rollout %/override map, and
///     updating Enabled / RolloutPercentage / Overrides for one flag.</description></item>
///   <item><description>Tenant: <c>GET /api/me/features</c> — any authed user;
///     returns the resolved <c>{ key: bool }</c> for the caller's tenant.
///     This is what the SPA reads to gate UI surfaces.</description></item>
/// </list>
/// A dev-only <c>PUT /api/test/feature-flags/{key}</c> exists in
/// <see cref="TestSupportEndpoints"/> so the E2E suite can force a flag for
/// the calling tenant in a kill-switch test without needing SuperAdmin auth.
/// </remarks>
public static class FeatureFlagEndpoints
{
    public static IEndpointRouteBuilder MapFeatureFlagEndpoints(this IEndpointRouteBuilder routes)
    {
        // ── Admin: SuperAdmin only ─────────────────────────────────────────────
        // Lives UNDER /api/platform so it inherits the TenantResolutionMiddleware
        // SuperAdmin pathway (any other /api/* path 400s a SuperAdmin token).
        var admin = routes.MapGroup("/api/platform/feature-flags").RequireAuthorization();
        admin.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.User.Role() == BidBuilder.Api.Models.UserRole.SuperAdmin
                ? await next(ctx)
                : Results.Json(new { error = "Feature-flag administration requires a SuperAdmin." }, statusCode: 403));

        admin.MapGet("", async (IFeatureService feat, CancellationToken ct) =>
        {
            var flags = await feat.ListAsync(ct);
            return Results.Ok(flags.Select(ToDto).ToList());
        });

        admin.MapPut("{key}", async (string key, FeatureFlagInput input, AppDbContext db, IFeatureService feat, CancellationToken ct) =>
        {
            var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Key == key, ct);
            if (flag is null) return Results.NotFound();
            if (input.Description is not null) flag.Description = input.Description;
            if (input.Enabled is { } e) flag.Enabled = e;
            if (input.RolloutPercentage is { } r)
            {
                if (r < 0 || r > 100)
                    return Results.BadRequest(new { error = "RolloutPercentage must be 0..100" });
                flag.RolloutPercentage = r;
            }
            if (input.Overrides is not null) flag.Overrides = input.Overrides;
            flag.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // Invalidate the catalogue cache so the new value is visible on the
            // next request, not whenever the 30s TTL happens to expire.
            feat.Invalidate();
            return Results.Ok(ToDto(flag));
        });

        // ── Tenant: any authed user — what the SPA reads to gate UI ────────────
        routes.MapGet("/api/me/features", async (IFeatureService feat, ITenantContext tenant, CancellationToken ct) =>
        {
            // For the unauthenticated / pre-tenant edge case (empty TenantId),
            // return an empty map — the SPA treats missing keys as "false".
            if (tenant.TenantId == Guid.Empty) return Results.Ok(new Dictionary<string, bool>());
            var resolved = await feat.ResolveAllAsync(tenant.TenantId, ct);
            return Results.Ok(resolved);
        }).RequireAuthorization();

        return routes;
    }

    private static FeatureFlagDto ToDto(FeatureFlag f) =>
        new(f.Id, f.Key, f.Description, f.Enabled, f.RolloutPercentage,
            new Dictionary<string, bool>(f.Overrides), f.CreatedAt, f.UpdatedAt);
}
