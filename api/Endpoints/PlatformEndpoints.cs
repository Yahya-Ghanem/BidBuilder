using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ───────────────────────────────────────────────────────────────────────
public record PlatformTenantDto(
    Guid Id, string Slug, string Name, string DefaultLocale, bool IsSuspended,
    DateTime CreatedAt, int UserCount, int ProjectCount);

public record CreateTenantInput(
    string? Slug, string? Name, string? DefaultLocale,
    string? AdminName, string? AdminEmail, string? AdminPassword);

/// <summary>
/// Platform administration, reserved for SuperAdmins (tenant-less operators). These
/// routes manage <em>tenants</em> only — never a tenant's business data — so they run
/// with no resolved tenant context and read cross-tenant aggregates with the query
/// filters turned off. The SuperAdmin gate is enforced both here (a role filter) and
/// in <see cref="Tenancy.TenantResolutionMiddleware"/> (which lets only a SuperAdmin
/// token reach /api/platform without a tenant).
/// </summary>
public static class PlatformEndpoints
{
    private const int MinPasswordLength = 8;
    private static readonly string[] Locales = ["en", "ar"];

    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/platform").RequireAuthorization();

        // SuperAdmin-only — strictly the platform role (a TenantAdmin must NOT pass).
        grp.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.User.Role() == UserRole.SuperAdmin
                ? await next(ctx)
                : Results.Json(new { error = "Platform administration requires a SuperAdmin." }, statusCode: 403));

        // ── List every tenant with its user / project counts ──────────────────
        grp.MapGet("/tenants", async (AppDbContext db) =>
        {
            var tenants = await db.Tenants.OrderBy(t => t.Name).ToListAsync();

            var userCounts = await db.Users.IgnoreQueryFilters()
                .Where(u => u.TenantId != null)
                .GroupBy(u => u.TenantId!.Value)
                .Select(g => new { TenantId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TenantId, x => x.Count);

            var projectCounts = await db.Projects.IgnoreQueryFilters()
                .GroupBy(p => p.TenantId)
                .Select(g => new { TenantId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TenantId, x => x.Count);

            return Results.Ok(tenants.Select(t => new PlatformTenantDto(
                t.Id, t.Slug, t.Name, t.DefaultLocale, t.IsSuspended, t.CreatedAt,
                userCounts.TryGetValue(t.Id, out var uc) ? uc : 0,
                projectCounts.TryGetValue(t.Id, out var pc) ? pc : 0)).ToList());
        });

        // ── Create a tenant (+ optionally its first admin) ─────────────────────
        grp.MapPost("/tenants", async (CreateTenantInput i, AppDbContext db, ITenantContext tc) =>
        {
            var slug = (i.Slug ?? "").Trim().ToLowerInvariant();
            var name = (i.Name ?? "").Trim();
            var locale = (i.DefaultLocale ?? "en").Trim().ToLowerInvariant();
            if (!IsValidSlug(slug)) return Bad("Slug must be 2–64 chars of lowercase letters, digits or hyphens.");
            if (name.Length == 0) return Bad("Name is required.");
            if (!Locales.Contains(locale)) return Bad("Locale must be 'en' or 'ar'.");
            if (await db.Tenants.AnyAsync(x => x.Slug == slug)) return Conflict("A tenant with that slug already exists.");

            // Validate the optional first-admin fields up front (before any write).
            var adminEmail = (i.AdminEmail ?? "").Trim().ToLowerInvariant();
            var adminName  = (i.AdminName ?? "").Trim();
            var wantAdmin  = adminEmail.Length > 0 || (i.AdminPassword ?? "").Length > 0 || adminName.Length > 0;
            if (wantAdmin)
            {
                if (!LooksLikeEmail(adminEmail)) return Bad("A valid admin email is required.");
                if (adminName.Length == 0) return Bad("Admin name is required.");
                if ((i.AdminPassword ?? "").Length < MinPasswordLength)
                    return Bad($"Admin password must be at least {MinPasswordLength} characters.");
                // Email is unique per tenant; a brand-new tenant can't collide, but guard
                // against any platform-wide reuse (including a SuperAdmin's) that would
                // confuse later logins and audit trails.
                if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == adminEmail))
                    return Conflict("A user with that email already exists.");
            }

            var t = new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = name, DefaultLocale = locale };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();

            // Resolve the context to the new tenant so the shared seeding routine's
            // query filters + auto-stamp target it.
            tc.Set(t.Id, t.Slug);
            var adminGroup = await DbInitializer.SeedTenantDefaultsAsync(db, t);

            if (wantAdmin)
            {
                var admin = new User
                {
                    TenantId = t.Id, Email = adminEmail, Name = adminName,
                    Role = UserRole.TenantAdmin, IsActive = true,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(i.AdminPassword),
                };
                db.Users.Add(admin);
                await db.SaveChangesAsync();
                db.UserGroups.Add(new UserGroup { TenantId = t.Id, UserId = admin.Id, GroupId = adminGroup.Id });
                await db.SaveChangesAsync();
            }

            var dto = new PlatformTenantDto(t.Id, t.Slug, t.Name, t.DefaultLocale, t.IsSuspended, t.CreatedAt,
                wantAdmin ? 1 : 0, 0);
            return Results.Created($"/api/platform/tenants/{t.Id}", dto);
        });

        // ── Suspend / re-activate a tenant ─────────────────────────────────────
        grp.MapPost("/tenants/{id:guid}/suspend", (Guid id, AppDbContext db) => SetSuspended(db, id, true));
        grp.MapPost("/tenants/{id:guid}/activate", (Guid id, AppDbContext db) => SetSuspended(db, id, false));
    }

    private static async Task<IResult> SetSuspended(AppDbContext db, Guid id, bool suspended)
    {
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Results.NotFound();
        t.IsSuspended = suspended;   // Tenant isn't IHasTenant — no resolved tenant needed to save.
        await db.SaveChangesAsync();
        return Results.Ok(new { t.Id, t.Slug, t.IsSuspended });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static IResult Bad(string msg) => Results.Json(new { error = msg }, statusCode: 400);
    private static IResult Conflict(string msg) => Results.Json(new { error = msg }, statusCode: 409);
    private static bool LooksLikeEmail(string e) => e.Length >= 3 && e.Contains('@') && !e.StartsWith('@') && !e.EndsWith('@');
    private static bool IsValidSlug(string s) => Regex.IsMatch(s, "^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$");
}
