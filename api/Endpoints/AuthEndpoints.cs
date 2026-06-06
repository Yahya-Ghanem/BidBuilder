using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

/// <summary>Credentials, plus an optional second factor (20.8). When the account
/// has 2FA enabled, supply exactly one of <see cref="TotpCode"/> (authenticator
/// app) or <see cref="RecoveryCode"/> (single-use backup).</summary>
public record LoginRequest(string Email, string Password, string? TotpCode = null, string? RecoveryCode = null);
public record LoginResponse(string Token, DateTime ExpiresAt, UserDto User);
public record UserDto(int Id, string Name, string Email, string Role);

// ── 2FA (TOTP) DTOs (20.8) ───────────────────────────────────────────────────
public record TotpCodeInput(string? Code);
public record TwoFactorStatus(bool Enabled, bool Pending, int RecoveryCodesRemaining);
public record TotpSetupResponse(string Secret, string OtpauthUri);
public record RecoveryCodesResponse(string[] RecoveryCodes);

public record ModulePermissionDto(string Code, string Name, bool CanView, bool CanAdd, bool CanEdit, bool CanDelete);
public record MePermissionsResponse(string Role, bool IsAdmin, List<ModulePermissionDto> Modules);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth");

        // POST /api/auth/login — tenant comes from X-Tenant-Id header (or, once
        // logged in, the JWT). Returns a JWT carrying tenant_slug + role.
        grp.MapPost("/login", async (LoginRequest req, AppDbContext db, ITenantContext tenant, JwtService jwt) =>
        {
            // A suspended workspace blocks all of its members — checked before any
            // credential work so a suspended tenant is a hard stop.
            var workspace = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenant.TenantId);
            if (workspace is null || workspace.IsSuspended)
                return Results.Json(new { error = "This workspace is suspended. Contact your administrator." }, statusCode: 403);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == req.Email.Trim().ToLower());
            if (user is null || !user.IsActive)
                return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);

            if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);

            // Second factor (20.8): a correct password alone is not enough once the
            // account has 2FA fully enabled — require a current TOTP code or a single-use
            // recovery code. The password is verified FIRST so we never reveal whether
            // 2FA is on for an account whose password is wrong.
            if (user.TotpSecret is { Length: > 0 } && user.TotpEnabledAt is not null)
            {
                var hasTotp = !string.IsNullOrWhiteSpace(req.TotpCode);
                var hasRecovery = !string.IsNullOrWhiteSpace(req.RecoveryCode);
                if (!hasTotp && !hasRecovery)
                    return Results.Json(new { mfaRequired = true }, statusCode: 200);

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var ok = hasTotp
                    ? TotpService.Verify(user.TotpSecret, req.TotpCode, now)
                    : TryConsumeRecoveryCode(user, req.RecoveryCode!);
                if (!ok)
                    return Results.Json(new { error = "Invalid authentication code", mfaRequired = true }, statusCode: 401);
            }

            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();   // also persists a consumed recovery code

            var (token, expires) = jwt.Issue(user, tenant.TenantSlug);
            return Results.Ok(new LoginResponse(
                token, expires,
                new UserDto(user.Id, user.Name, user.Email, user.Role.ToString())));
        })
        .AllowAnonymous()
        .RequireRateLimiting("login");

        // POST /api/auth/platform-login — SuperAdmin sign-in. SuperAdmins have no tenant,
        // so this needs no X-Tenant-Id and is exempt from tenant resolution in the
        // middleware. The lookup bypasses the tenant query filter and matches only a
        // platform SuperAdmin (TenantId null). Same generic 401 on any failure.
        grp.MapPost("/platform-login", async (LoginRequest req, AppDbContext db, JwtService jwt) =>
        {
            var email = req.Email.Trim().ToLower();
            var user = await db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email == email && u.TenantId == null && u.Role == UserRole.SuperAdmin);
            if (user is null || !user.IsActive)
                return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);

            if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);

            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var (token, expires) = jwt.IssuePlatform(user);
            return Results.Ok(new LoginResponse(
                token, expires,
                new UserDto(user.Id, user.Name, user.Email, user.Role.ToString())));
        })
        .AllowAnonymous()
        .RequireRateLimiting("login");

        // GET /api/auth/permissions — the caller's effective module permissions, so
        // the UI can hide affordances the API would reject anyway. Admins get every
        // seeded module with full rights; others get the OR of their teams' GroupModule
        // flags. The API still enforces on every mutation — this is convenience only.
        grp.MapGet("/permissions", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var modules = await db.Modules.OrderBy(m => m.Code)
                .Select(m => new { m.Id, m.Code, m.Name })
                .ToListAsync();

            if (me.IsAdmin())
            {
                var all = modules
                    .Select(m => new ModulePermissionDto(m.Code, m.Name, true, true, true, true))
                    .ToList();
                return Results.Ok(new MePermissionsResponse(me.Role().ToString(), true, all));
            }

            var uid = me.Id();
            var flags = await (
                from ug in db.UserGroups.Where(x => x.UserId == uid)
                join gm in db.GroupModules on ug.GroupId equals gm.GroupId
                select gm).ToListAsync();

            var byModule = flags
                .GroupBy(f => f.ModuleId)
                .ToDictionary(g => g.Key, g => (
                    View:   g.Any(x => x.CanView),
                    Add:    g.Any(x => x.CanAdd),
                    Edit:   g.Any(x => x.CanEdit),
                    Delete: g.Any(x => x.CanDelete)));

            var list = modules.Select(m =>
            {
                byModule.TryGetValue(m.Id, out var f);
                return new ModulePermissionDto(m.Code, m.Name, f.View, f.Add, f.Edit, f.Delete);
            }).ToList();

            return Results.Ok(new MePermissionsResponse(me.Role().ToString(), false, list));
        })
        .RequireAuthorization();

        MapTwoFactor(app);
    }

    // ── Two-factor authentication (TOTP) — self-service, per user (20.8) ─────────
    private static void MapTwoFactor(IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth/2fa").RequireAuthorization();

        // Where the account stands: fully enabled, mid-enrolment (secret minted but
        // not yet confirmed), and how many backup codes remain.
        grp.MapGet("/status", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == me.Id());
            if (u is null) return Results.NotFound();
            var enabled = u.TotpSecret is { Length: > 0 } && u.TotpEnabledAt is not null;
            var pending = u.TotpSecret is { Length: > 0 } && u.TotpEnabledAt is null;
            return Results.Ok(new TwoFactorStatus(enabled, pending, ReadHashes(u.TotpRecoveryCodesJson).Count));
        });

        // Begin enrolment: mint a fresh secret (NOT yet active) and return the
        // otpauth:// URI + base32 secret for the authenticator app. Re-callable while
        // pending (regenerates), but refused once 2FA is already active.
        grp.MapPost("/setup", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == me.Id());
            if (u is null) return Results.NotFound();
            if (u.TotpEnabledAt is not null)
                return Results.Json(new { error = "Two-factor is already enabled. Disable it first to re-enroll." }, statusCode: 409);

            var secret = TotpService.NewSecret();
            u.TotpSecret = secret;
            u.TotpEnabledAt = null;
            u.TotpRecoveryCodesJson = null;
            await db.SaveChangesAsync();

            return Results.Ok(new TotpSetupResponse(
                TotpService.Base32Encode(secret),
                TotpService.OtpAuthUri("BidBuilder", u.Email, secret)));
        });

        // Confirm enrolment: verify a code against the pending secret, switch 2FA on,
        // and return the one-time recovery codes (shown exactly once).
        grp.MapPost("/enable", async (TotpCodeInput input, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == me.Id());
            if (u is null) return Results.NotFound();
            if (u.TotpEnabledAt is not null)
                return Results.Json(new { error = "Two-factor is already enabled." }, statusCode: 409);
            if (u.TotpSecret is not { Length: > 0 })
                return Results.Json(new { error = "Start setup before enabling." }, statusCode: 400);
            if (!TotpService.Verify(u.TotpSecret, input.Code, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                return Results.Json(new { error = "That code didn't match. Check your authenticator and try again." }, statusCode: 400);

            var codes = TotpService.NewRecoveryCodes();
            u.TotpEnabledAt = DateTime.UtcNow;
            u.TotpRecoveryCodesJson = JsonSerializer.Serialize(
                codes.Select(c => BCrypt.Net.BCrypt.HashPassword(TotpService.NormalizeRecoveryCode(c))).ToList());
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "user.2fa-enabled", "User", u.Id.ToString(), "Two-factor authentication enabled");

            return Results.Ok(new RecoveryCodesResponse([.. codes]));
        });

        // Turn 2FA off. Requires proof — a current TOTP code or a recovery code — so a
        // walked-away session can't silently strip the second factor. Bumps TokenVersion
        // to sign out other sessions.
        grp.MapPost("/disable", async (TotpCodeInput input, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == me.Id());
            if (u is null) return Results.NotFound();
            if (u.TotpEnabledAt is null || u.TotpSecret is not { Length: > 0 })
                return Results.Json(new { error = "Two-factor is not enabled." }, statusCode: 400);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var ok = TotpService.Verify(u.TotpSecret, input.Code, now) || TryConsumeRecoveryCode(u, input.Code ?? "");
            if (!ok)
                return Results.Json(new { error = "Enter a valid authentication or recovery code to disable." }, statusCode: 400);

            u.TotpSecret = null;
            u.TotpEnabledAt = null;
            u.TotpRecoveryCodesJson = null;
            u.TokenVersion += 1;   // invalidate other outstanding sessions
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "user.2fa-disabled", "User", u.Id.ToString(), "Two-factor authentication disabled");
            return Results.NoContent();
        });
    }

    // ── Recovery-code storage helpers ────────────────────────────────────────────
    private static List<string> ReadHashes(string? json) =>
        string.IsNullOrEmpty(json) ? [] : (JsonSerializer.Deserialize<List<string>>(json) ?? []);

    /// <summary>If <paramref name="code"/> matches an unused recovery hash, consume it
    /// (remove from the list, mutate the user) and return true. The caller persists.</summary>
    private static bool TryConsumeRecoveryCode(User user, string code)
    {
        var norm = TotpService.NormalizeRecoveryCode(code);
        if (string.IsNullOrEmpty(norm)) return false;
        var hashes = ReadHashes(user.TotpRecoveryCodesJson);
        var idx = hashes.FindIndex(h => BCrypt.Net.BCrypt.Verify(norm, h));
        if (idx < 0) return false;
        hashes.RemoveAt(idx);
        user.TotpRecoveryCodesJson = JsonSerializer.Serialize(hashes);
        return true;
    }
}
