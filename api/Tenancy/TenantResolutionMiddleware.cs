using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;

namespace BidBuilder.Api.Tenancy;

/// <summary>
/// Resolves the current tenant from the incoming request and populates
/// <see cref="ITenantContext"/> before downstream handlers run.
///
/// Resolution priority:
///   1. JWT claim "tenant_slug"
///   2. X-Tenant-Id header (slug)
///   3. "default" tenant (Development only — strict 401 in Production)
///
/// Only requests under /api are scoped; health probes and static assets pass through.
/// </summary>
public class TenantResolutionMiddleware(
    RequestDelegate next,
    ILogger<TenantResolutionMiddleware> logger)
{
    private const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db, ITenantContext tenantCtx, IHostEnvironment env)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        // 1. JWT claim-based resolution (real auth path)
        string? slug = null;
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            slug = ctx.User.FindFirst("tenant_slug")?.Value;
        }

        // 2. Header-based fallback (server-to-server calls, dev tooling)
        slug ??= ctx.Request.Headers[TenantHeader].FirstOrDefault()?.Trim();

        // 3. Default tenant in dev so the demo "just works"
        if (string.IsNullOrWhiteSpace(slug))
        {
            if (!env.IsDevelopment())
            {
                logger.LogWarning("Request to {Path} missing tenant claim/header", path);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "Tenant context required (sign in or pass X-Tenant-Id)",
                });
                return;
            }
            slug = "default";
        }

        var tenant = await db.Tenants.AsNoTracking()
                                     .FirstOrDefaultAsync(t => t.Slug == slug);

        if (tenant is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = $"Unknown tenant '{slug}'" });
            return;
        }

        tenantCtx.Set(tenant.Id, tenant.Slug);
        await next(ctx);
    }
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantResolutionMiddleware>();
}
