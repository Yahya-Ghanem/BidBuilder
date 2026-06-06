using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

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

        // Platform SuperAdmin sign-in is tenant-less and anonymous — let it through
        // before any tenant resolution (it carries no tenant claim or header).
        if (path.StartsWith("/api/auth/platform-login", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        // Public subcontractor quote portal (20.6) is anonymous and carries no tenant
        // claim/header — the request is authenticated only by the unguessable token in
        // the URL. The handler resolves the owning tenant from that token (a globally
        // unique value), so let it through with the tenant deliberately unresolved.
        if (path.StartsWith("/api/portal", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        // SAML SSO flow (20.8b) is anonymous and carries no tenant claim/header — the
        // tenant is identified by the {slug} in the path (/api/auth/sso/{slug}/...).
        // The handlers resolve that slug to a tenant and set the context themselves
        // (mirroring the public portal), so let the request through unresolved here.
        if (path.StartsWith("/api/auth/sso", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        // SuperAdmins operate at the platform level with NO tenant scope. Their token
        // carries role=SuperAdmin and an empty tenant. Let them reach the /api/platform
        // endpoints with the tenant context deliberately unresolved (those endpoints
        // work on non-tenant-scoped data). Any other /api route would fault on the
        // unresolved tenant, so refuse it up front with a clear message instead.
        if (ctx.User.Identity?.IsAuthenticated == true &&
            string.Equals(ctx.User.FindFirst("role")?.Value, "SuperAdmin", StringComparison.Ordinal))
        {
            if (path.StartsWith("/api/platform", StringComparison.OrdinalIgnoreCase))
            {
                await next(ctx);
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "SuperAdmin sessions operate only on the /api/platform endpoints.",
            });
            return;
        }

        // 1. JWT claim-based resolution (real auth path)
        string? slug = null;
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            slug = ctx.User.FindFirst("tenant_slug")?.Value;
        }

        // 2. Custom-domain resolution (20.11). When the caller carries no tenant claim
        //    (e.g. the pre-auth /api/auth/login request from a vanity host), match the
        //    request Host against a tenant's registered CustomDomain. This is what lets
        //    a workspace be reached at bids.acme.com without passing X-Tenant-Id. The
        //    platform's own host is never a CustomDomain, so it falls through.
        Tenant? tenant = null;
        if (string.IsNullOrWhiteSpace(slug))
        {
            var host = ctx.Request.Host.Host?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(host))
            {
                tenant = await db.Tenants.AsNoTracking()
                                         .FirstOrDefaultAsync(t => t.CustomDomain == host);
                if (tenant is not null) slug = tenant.Slug;
            }
        }

        // 3. Header-based fallback (server-to-server calls, dev tooling)
        slug ??= ctx.Request.Headers[TenantHeader].FirstOrDefault()?.Trim();

        // 4. Default tenant in dev so the demo "just works"
        if (string.IsNullOrWhiteSpace(slug))
        {
            if (!env.IsDevelopment())
            {
                logger.LogWarning("Request to {Path} missing tenant claim/header", path);
                await WriteTenantRequiredAsync(ctx);
                return;
            }
            slug = "default";
        }

        // Already loaded via the custom-domain match? Otherwise resolve by slug.
        tenant ??= await db.Tenants.AsNoTracking()
                                   .FirstOrDefaultAsync(t => t.Slug == slug);

        if (tenant is null)
        {
            // Respond identically to the missing-tenant case — a generic 401 that never
            // confirms whether a given slug exists and never echoes the supplied value.
            // This removes the tenant-slug enumeration oracle (an unauthenticated caller
            // could otherwise tell a real slug from a fake one by the 404 vs proceed).
            // The attempted slug is logged server-side only, for ops.
            logger.LogWarning("Request to {Path} with unresolved tenant slug {Slug}", path, slug);
            await WriteTenantRequiredAsync(ctx);
            return;
        }

        // A suspended workspace is a hard kill-switch for EVERY data route, not just new
        // logins — an already-issued token (up to 8h) must stop working the moment its
        // tenant is suspended. /api/auth/* is exempted so the login handler can return its
        // own friendly 403 (and so a re-activated tenant's users can sign back in).
        if (tenant.IsSuspended && !path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Request to {Path} blocked — tenant {Slug} is suspended", path, tenant.Slug);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "This workspace is suspended. Contact your administrator." });
            return;
        }

        tenantCtx.Set(tenant.Id, tenant.Slug);
        await next(ctx);
    }

    private static Task WriteTenantRequiredAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return ctx.Response.WriteAsJsonAsync(new
        {
            error = "Tenant context required (sign in or pass a valid X-Tenant-Id).",
        });
    }
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantResolutionMiddleware>();
}
