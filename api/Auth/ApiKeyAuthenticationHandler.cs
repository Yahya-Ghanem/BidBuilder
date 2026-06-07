using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using BidBuilder.Api.Data;

namespace BidBuilder.Api.Auth;

/// <summary>
/// 21.2 — Secret generation + hashing for programmatic API keys. The raw secret is
/// shown to the creator exactly once; only its SHA-256 hash is persisted.
/// </summary>
public static class ApiKeyTokens
{
    public const string Header = "X-Api-Key";
    public const string Scheme = "ApiKey";

    /// <summary>Lowercase-hex SHA-256 of the secret. Unsalted is fine — the secret is
    /// 256 bits of CSPRNG entropy, so there is nothing to brute-force.</summary>
    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>A fresh secret (<c>bbk_&lt;43 url-safe chars&gt;</c>) and its non-secret display prefix.</summary>
    public static (string Secret, string Prefix) Generate()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var secret = $"bbk_{raw}";
        return (secret, secret[..Math.Min(14, secret.Length)]);
    }
}

/// <summary>
/// 21.2 — Authenticates a request bearing an <c>X-Api-Key</c> header. The key
/// resolves to its owning user; the handler builds the SAME claim set a JWT login
/// would (sub / email / name / role / tenant_slug), so every downstream layer —
/// <see cref="Tenancy.TenantResolutionMiddleware"/>, RBAC, audit — works unchanged.
///
/// Registered alongside JWT via a policy scheme that forwards here only when the
/// header is present (see Program.cs). Lookups bypass the tenant query filter because
/// the tenant context isn't resolved yet at authentication time.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyTokens.Header, out var values))
            return AuthenticateResult.NoResult();
        var presented = values.ToString().Trim();
        if (presented.Length == 0) return AuthenticateResult.NoResult();

        var db = Context.RequestServices.GetRequiredService<AppDbContext>();
        var hash = ApiKeyTokens.Hash(presented);
        var key = await db.ApiKeys.IgnoreQueryFilters().FirstOrDefaultAsync(k => k.KeyHash == hash);
        if (key is null) return AuthenticateResult.Fail("Invalid API key.");

        var now = DateTime.UtcNow;
        if (key.RevokedAt is not null) return AuthenticateResult.Fail("API key has been revoked.");
        if (key.ExpiresAt is { } exp && exp <= now) return AuthenticateResult.Fail("API key has expired.");

        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == key.UserId);
        if (user is null || !user.IsActive) return AuthenticateResult.Fail("API key owner is inactive.");

        var slug = await db.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == key.TenantId).Select(t => t.Slug).FirstOrDefaultAsync() ?? "";

        // Throttled last-used stamp — avoid a DB write on every single call. This is a
        // MODIFIED (not Added) IHasTenant row, so the SaveChanges auto-stamp leaves its
        // TenantId untouched even though the tenant context isn't resolved yet.
        if (key.LastUsedAt is null || now - key.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            key.LastUsedAt = now;
            try { await db.SaveChangesAsync(); }
            catch (Exception ex) { Logger.LogDebug(ex, "API key last-used stamp failed"); }
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.Name),
            new("role", user.Role.ToString()),
            new(JwtService.TenantSlugClaim, slug),
            new(JwtService.TenantIdClaim, key.TenantId.ToString()),
            // 22.2 — let ApiKeyGuard enforce scopes + rate limit without re-reading the DB.
            new(ApiKeyClaims.KeyId, key.Id.ToString()),
            new(ApiKeyClaims.Scopes, key.Scopes ?? "read,write"),
            new(ApiKeyClaims.RateLimit, key.RateLimitPerMinute?.ToString() ?? ""),
        };
        var identity = new ClaimsIdentity(claims, ApiKeyTokens.Scheme, nameType: "name", roleType: "role");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyTokens.Scheme);
        return AuthenticateResult.Success(ticket);
    }
}
