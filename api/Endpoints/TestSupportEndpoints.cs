using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

/// <summary>What a freshly-minted E2E user gets back: a ready-to-use token (issued
/// directly, NOT via the rate-limited /api/auth/login) plus the raw credentials so
/// a spec that exercises the real login UI can sign in as its own user.</summary>
public record TestUserResponse(string Token, DateTime ExpiresAt, UserDto User, string Email, string Password);

/// <summary>
/// 29.A.1 — E2E test support. Mints a throwaway per-spec user so Playwright specs
/// stop sharing admin@bidbuilder.local (13 parallel UI logins from one runner IP
/// tripped the 10/60s login rate limit; the f835459 "fix" loosened the limit to
/// 200 — a security control papering over fragile tests).
///
/// The token is issued here directly via <see cref="JwtService"/>, so provisioning
/// puts ZERO pressure on the login rate limiter — only specs that deliberately test
/// the login UI perform a real /api/auth/login, and those fit in the default 10/60s.
///
/// Mapped ONLY when the environment is Development (see Program.cs): in Production
/// the route does not exist and returns 404. api.Tests boots the host with
/// UseEnvironment("Production"), and <c>TestSupportEndpointsTests</c> pins that.
/// </summary>
public static class TestSupportEndpoints
{
    /// <summary>Every minted user's email starts with this; the DELETE below refuses
    /// to touch anything else, so the endpoint can never delete a real account.</summary>
    public const string EmailPrefix = "e2e-";

    public static void MapTestSupportEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/test");

        // POST /api/test/users — anonymous like /api/auth/login (the caller has no
        // token yet); the tenant comes from the X-Tenant-Id header the same way.
        grp.MapPost("/users", async (AppDbContext db, ITenantContext tenant, JwtService jwt) =>
        {
            var suffix   = Guid.NewGuid().ToString("N")[..8];
            var email    = $"{EmailPrefix}{suffix}@bidbuilder.test";
            var password = $"E2e-{Guid.NewGuid():N}";

            // TenantAdmin: the specs drive admin surfaces (Settings, Users & Teams,
            // Audit) — a scoped TenantUser would 403 half the suite.
            var user = new User
            {
                TenantId     = tenant.TenantId,
                Email        = email,
                Name         = $"E2E {suffix}",
                Role         = UserRole.TenantAdmin,
                IsActive     = true,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var (token, expires) = jwt.Issue(user, tenant.TenantSlug);
            return Results.Ok(new TestUserResponse(
                token, expires,
                new UserDto(user.Id, user.Name, user.Email, user.Role.ToString()),
                email, password));
        })
        .AllowAnonymous();

        // DELETE /api/test/users/{id} — spec teardown. The EmailPrefix guard means a
        // wrong id can only ever remove another throwaway, never the seeded admin.
        grp.MapDelete("/users/{id:int}", async (int id, AppDbContext db) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Id == id && u.Email.StartsWith(EmailPrefix));
            if (user is null) return Results.NotFound();

            db.Users.Remove(user);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .AllowAnonymous();

        // 29.B.1 — Force a feature flag to a specific value for the calling
        // tenant. The real admin endpoint (PUT /api/feature-flags/{key}) needs
        // SuperAdmin; the kill-switch E2E mints a TenantAdmin, so it routes
        // through here instead. Only ever mapped in Development.
        grp.MapPut("/feature-flags/{key}/override", async (string key, ForceFlagInput input, AppDbContext db, ITenantContext tenant, IFeatureService feat) =>
        {
            var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Key == key);
            if (flag is null) return Results.NotFound();
            var slug = await db.Tenants.IgnoreQueryFilters()
                .Where(t => t.Id == tenant.TenantId).Select(t => t.Slug).FirstOrDefaultAsync()
                ?? string.Empty;
            if (string.IsNullOrEmpty(slug)) return Results.BadRequest();
            if (input.Enabled is null) flag.Overrides.Remove(slug);
            else flag.Overrides[slug] = input.Enabled.Value;
            flag.UpdatedAt = DateTime.UtcNow;
            // Dictionary value-converter needs a fresh reference for EF to detect
            // the mutation reliably across providers — copy via the comparer.
            flag.Overrides = new Dictionary<string, bool>(flag.Overrides);
            await db.SaveChangesAsync();
            feat.Invalidate();
            return Results.Ok(new { key, slug, enabled = input.Enabled });
        })
        .AllowAnonymous();
    }
}

/// <summary>Dev-only payload for overriding a flag for the current tenant.
/// <c>Enabled=null</c> clears any existing override.</summary>
public record ForceFlagInput(bool? Enabled);
