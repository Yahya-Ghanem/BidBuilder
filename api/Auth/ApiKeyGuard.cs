using System.Security.Claims;

namespace BidBuilder.Api.Auth;

/// <summary>
/// 22.2 — Enforces API-key scopes + per-key rate limits for requests authenticated by an
/// <c>X-Api-Key</c> header. Runs only for ApiKey-authenticated requests (identified by the
/// <see cref="ApiKeyClaims.KeyId"/> claim the auth handler stamps); JWT/anonymous requests
/// pass straight through, so no existing endpoint behavior changes.
///
/// For an API-key request it, in order:
///   1. counts the request against the key's per-minute limit → 429 (+ Retry-After) on breach;
///   2. checks the HTTP method's required scope (read for safe methods, write for mutations)
///      → 403 if the key wasn't granted it.
/// Both checks read claims only (limit + scopes are stamped at authentication time), so there
/// is no extra DB hit on the hot path. The scope check is method-based, so it layers on top of
/// the existing RBAC (PermissionService) without touching any endpoint.
/// </summary>
public sealed class ApiKeyGuard(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, ApiKeyRateLimiter limiter)
    {
        var keyIdClaim = ctx.User?.FindFirst(ApiKeyClaims.KeyId)?.Value;
        if (keyIdClaim is null || !int.TryParse(keyIdClaim, out var keyId))
        {
            await next(ctx);   // not an API-key request — nothing to enforce
            return;
        }

        // ── Rate limit ────────────────────────────────────────────────────────────
        int? limit = int.TryParse(ctx.User!.FindFirst(ApiKeyClaims.RateLimit)?.Value, out var rl) ? rl : null;
        var rr = limiter.Check(keyId, limit, DateTime.UtcNow);
        if (rr.Limit is { } lim)
        {
            ctx.Response.Headers["X-RateLimit-Limit"] = lim.ToString();
            ctx.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, rr.Remaining ?? 0).ToString();
        }
        if (!rr.Allowed)
        {
            ctx.Response.Headers["Retry-After"] = rr.RetryAfterSeconds.ToString();
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "API key rate limit exceeded.",
                retryAfterSeconds = rr.RetryAfterSeconds,
            });
            return;
        }

        // ── IP allowlist (23.2) ──────────────────────────────────────────────────
        // Evaluated AFTER the rate limit (so a flood from a blocked IP still gets a 429
        // for the right reason) and BEFORE the scope check (an IP rejection is a stronger
        // signal — wrong place, not just wrong method).
        var allowlist = ctx.User.FindFirst(ApiKeyClaims.IpAllowlist)?.Value;
        if (!CidrMatcher.IsAllowed(ctx.Connection.RemoteIpAddress, allowlist))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "API key is restricted to a specific IP allowlist and this request was rejected.",
            });
            return;
        }

        // ── Scope ─────────────────────────────────────────────────────────────────
        var required = IsWriteMethod(ctx.Request.Method) ? "write" : "read";
        var scopes = (ctx.User.FindFirst(ApiKeyClaims.Scopes)?.Value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(required, StringComparer.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = $"This API key lacks the '{required}' scope required for {ctx.Request.Method} requests.",
            });
            return;
        }

        await next(ctx);
    }

    private static bool IsWriteMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}

/// <summary>Claim names the <see cref="ApiKeyAuthenticationHandler"/> stamps so
/// <see cref="ApiKeyGuard"/> can enforce scopes + limits without re-reading the DB.</summary>
public static class ApiKeyClaims
{
    public const string KeyId       = "akey_id";
    public const string Scopes      = "akey_scope";
    public const string RateLimit   = "akey_rl";
    public const string IpAllowlist = "akey_ips";   // 23.2 — CSV of CIDRs; empty = any IP
}

public static class ApiKeyGuardExtensions
{
    public static IApplicationBuilder UseApiKeyGuard(this IApplicationBuilder app)
        => app.UseMiddleware<ApiKeyGuard>();
}
