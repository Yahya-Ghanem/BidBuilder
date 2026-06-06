using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

// ── Admin config DTOs (20.8b) ────────────────────────────────────────────────
/// <summary>What the admin UI sees. The certificate itself is never returned —
/// only whether one is stored — and the SP-side endpoints are echoed back so the
/// admin can register them with their IdP.</summary>
public record SamlConfigDto(
    bool Enabled, string IdpEntityId, string IdpSsoUrl, bool HasCertificate,
    string? EmailAttribute, string? NameAttribute, bool AllowJitProvisioning,
    string SpEntityId, string AcsUrl, string MetadataUrl, string LoginUrl);

/// <summary>Upsert payload. <see cref="IdpCertificatePem"/> is write-only and left
/// unchanged when null/blank on an existing config (so toggling Enabled doesn't
/// require re-pasting the cert).</summary>
public record SamlConfigInput(
    bool Enabled, string? IdpEntityId, string? IdpSsoUrl, string? IdpCertificatePem,
    string? EmailAttribute, string? NameAttribute, bool AllowJitProvisioning);

/// <summary>
/// 20.8b — SAML 2.0 single-sign-on. Two halves:
///   • Anonymous SP-initiated flow under <c>/api/auth/sso/{slug}/…</c> (metadata,
///     login redirect, ACS). These carry no JWT/header — the tenant is taken from
///     the URL slug — so the path is exempted from tenant-resolution middleware and
///     resolves + sets the tenant context itself (mirrors the 20.6 public portal).
///   • Tenant-admin config under <c>/api/sso/config</c> (authed; tenant resolved
///     normally).
/// All assertion validation + hardening lives in <see cref="SamlService"/>.
/// </summary>
public static class SsoEndpoints
{
    // Replay cache key prefix for single-use assertion IDs.
    private const string ReplayPrefix = "saml-assertion:";

    public static void MapSsoEndpoints(this IEndpointRouteBuilder app)
    {
        MapPublicSso(app);
        MapAdminConfig(app);
    }

    // ── Anonymous SP-initiated flow ────────────────────────────────────────────
    private static void MapPublicSso(IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth/sso");

        // SP metadata — an admin hands this to their IdP to register the service.
        grp.MapGet("/{slug}/metadata", async (string slug, HttpContext ctx, AppDbContext db, ITenantContext tc) =>
        {
            var (tenant, cfg, _) = await ResolveAsync(slug, db, tc);
            if (tenant is null || cfg is null || !cfg.Enabled) return Results.NotFound();
            var (spEntityId, acsUrl, _) = SpUrls(ctx, slug);
            return Results.Content(SamlService.BuildSpMetadata(spEntityId, acsUrl), "application/xml");
        }).AllowAnonymous();

        // Begin SP-initiated login: 302 to the IdP carrying the AuthnRequest.
        grp.MapGet("/{slug}/login", async (string slug, HttpContext ctx, AppDbContext db, ITenantContext tc, IConfiguration config) =>
        {
            var (tenant, cfg, _) = await ResolveAsync(slug, db, tc);
            if (tenant is null || cfg is null || !cfg.Enabled || tenant.IsSuspended)
                return Results.Redirect($"{WebBase(config)}/login?ssoError={Uri.EscapeDataString("Single sign-on is not available for this workspace.")}");

            var (spEntityId, acsUrl, _) = SpUrls(ctx, slug);
            // A fresh request ID; RelayState carries the slug so the ACS knows the tenant
            // even though many IdPs don't echo the original Destination.
            var requestId = "_" + Guid.NewGuid().ToString("N");
            var url = SamlService.BuildRedirectUrl(cfg, spEntityId, acsUrl, requestId, DateTime.UtcNow, relayState: slug);
            return Results.Redirect(url);
        }).AllowAnonymous();

        // Assertion Consumer Service — the IdP POSTs the signed SAML Response here.
        grp.MapPost("/{slug}/acs", async (
            string slug, HttpContext ctx, AppDbContext db, ITenantContext tc,
            JwtService jwt, AuditService audit, IMemoryCache cache, IConfiguration config) =>
        {
            var web = WebBase(config);
            IResult Fail(string msg) => Results.Redirect($"{web}/login?ssoError={Uri.EscapeDataString(msg)}");

            var (tenant, cfg, _) = await ResolveAsync(slug, db, tc);
            if (tenant is null || cfg is null || !cfg.Enabled) return Fail("Single sign-on is not available for this workspace.");
            if (tenant.IsSuspended) return Fail("This workspace is suspended. Contact your administrator.");

            if (!ctx.Request.HasFormContentType) return Fail("Malformed SSO response.");
            var form = await ctx.Request.ReadFormAsync();
            var samlResponse = form["SAMLResponse"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(samlResponse)) return Fail("Missing SAML response.");

            var (spEntityId, acsUrl, _) = SpUrls(ctx, slug);
            var result = SamlService.ValidateResponse(samlResponse!, cfg, spEntityId, acsUrl, DateTime.UtcNow);
            if (!result.Ok) return Fail(result.Error ?? "SAML assertion rejected.");

            // Single-use replay protection: reject a previously-seen assertion ID until
            // it would have expired anyway. Keyed per tenant so IDs can't collide across
            // workspaces. (In-memory — acceptable for a single API instance; a multi-node
            // deployment should back this with a distributed cache.)
            var replayKey = $"{ReplayPrefix}{tenant.Id}:{result.AssertionId}";
            if (cache.TryGetValue(replayKey, out _)) return Fail("This sign-in response was already used.");
            var ttl = (result.NotOnOrAfter ?? DateTime.UtcNow.AddMinutes(10)) - DateTime.UtcNow + SamlService.ClockSkew;
            if (ttl < TimeSpan.FromMinutes(1)) ttl = TimeSpan.FromMinutes(10);
            cache.Set(replayKey, true, ttl);

            // JIT user lookup / provisioning. Tenant context is set, so the query filter
            // scopes Users to this tenant automatically.
            var email = result.Email!;
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is null)
            {
                if (!cfg.AllowJitProvisioning)
                    return Fail("No account exists for this email and automatic provisioning is off.");
                user = new User
                {
                    TenantId = tenant.Id,
                    Email    = email,
                    Name     = result.DisplayName ?? email.Split('@')[0],
                    Role     = UserRole.TenantUser,
                    IsActive = true,
                    // SSO users have no usable local password — a random bcrypt hash means
                    // password login can never succeed for them (they reset to set one).
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
                };
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }
            else if (!user.IsActive)
            {
                return Fail("This account is deactivated.");
            }
            else if (result.DisplayName is { Length: > 0 } dn && user.Name != dn)
            {
                user.Name = dn;   // keep the display name fresh from the IdP
            }

            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await audit.LogAsync(PrincipalFor(user), "auth.sso-login", "User", user.Id.ToString(),
                $"SAML SSO sign-in via {cfg.IdpEntityId}");

            var (token, _) = jwt.Issue(user, tenant.Slug);
            // Hand the token to the SPA via the URL fragment (never the query string, so
            // it isn't logged by proxies or stored in server access logs).
            return Results.Redirect($"{web}/login/sso#token={Uri.EscapeDataString(token)}&tenant={Uri.EscapeDataString(tenant.Slug)}");
        }).AllowAnonymous();
    }

    // ── Tenant-admin config ────────────────────────────────────────────────────
    private static void MapAdminConfig(IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/sso").RequireAuthorization();

        grp.MapGet("/config", async (HttpContext ctx, ClaimsPrincipal me, AppDbContext db, ITenantContext tc) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can view SSO settings" }, statusCode: 403);
            var cfg = await db.SamlConfigs.AsNoTracking().FirstOrDefaultAsync();
            var slug = tc.TenantSlug;
            return Results.Ok(ToDto(ctx, slug, cfg));
        });

        grp.MapPut("/config", async (SamlConfigInput i, HttpContext ctx, ClaimsPrincipal me, AppDbContext db, ITenantContext tc, AuditService audit) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can change SSO settings" }, statusCode: 403);

            var cfg = await db.SamlConfigs.FirstOrDefaultAsync();
            var isNew = cfg is null;
            cfg ??= new TenantSamlConfig();

            var entityId = (i.IdpEntityId ?? "").Trim();
            var ssoUrl   = (i.IdpSsoUrl ?? "").Trim();
            var newCert  = string.IsNullOrWhiteSpace(i.IdpCertificatePem) ? null : i.IdpCertificatePem!.Trim();
            var effectiveCert = newCert ?? cfg.IdpCertificatePem;

            // Validate hard ONLY when the admin is turning SSO on — an admin may save a
            // half-filled draft with Enabled=false without tripping these.
            if (i.Enabled)
            {
                if (entityId.Length == 0) return Bad("IdP Entity ID is required to enable SSO.");
                if (!IsHttpUrl(ssoUrl))   return Bad("IdP SSO URL must be a valid http(s) URL.");
                if (string.IsNullOrWhiteSpace(effectiveCert)) return Bad("An IdP signing certificate is required to enable SSO.");
                if (!CertParses(effectiveCert)) return Bad("The IdP certificate could not be parsed (expected a PEM certificate).");
            }
            else if (newCert is not null && !CertParses(newCert))
            {
                return Bad("The IdP certificate could not be parsed (expected a PEM certificate).");
            }

            cfg.Enabled              = i.Enabled;
            cfg.IdpEntityId          = entityId;
            cfg.IdpSsoUrl            = ssoUrl;
            if (newCert is not null) cfg.IdpCertificatePem = newCert;
            cfg.EmailAttribute       = Trim(i.EmailAttribute);
            cfg.NameAttribute        = Trim(i.NameAttribute);
            cfg.AllowJitProvisioning = i.AllowJitProvisioning;
            cfg.UpdatedAt            = DateTime.UtcNow;
            if (isNew) db.SamlConfigs.Add(cfg);
            await db.SaveChangesAsync();

            await audit.LogAsync(me, "tenant.sso-config", "TenantSamlConfig", tc.TenantSlug,
                cfg.Enabled ? $"SSO enabled (IdP {cfg.IdpEntityId})" : "SSO disabled");

            return Results.Ok(ToDto(ctx, tc.TenantSlug, cfg));
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Resolve the tenant by slug and (only if present) load its SAML config,
    /// setting the tenant context so subsequent filtered queries scope correctly.
    /// Returns nulls for an unknown slug — callers map that to a generic failure.</summary>
    private static async Task<(Tenant? Tenant, TenantSamlConfig? Config, bool Set)> ResolveAsync(
        string slug, AppDbContext db, ITenantContext tc)
    {
        slug = (slug ?? "").Trim();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null) return (null, null, false);
        if (!tc.IsResolved) tc.Set(tenant.Id, tenant.Slug);
        var cfg = await db.SamlConfigs.FirstOrDefaultAsync();
        return (tenant, cfg, true);
    }

    /// <summary>The SP-side absolute URLs, derived from the request origin (or an
    /// explicit Saml:PublicBaseUrl override for deployments behind a proxy).</summary>
    private static (string SpEntityId, string AcsUrl, string MetadataUrl) SpUrls(HttpContext ctx, string slug)
    {
        var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var baseUrl = (config["Saml:PublicBaseUrl"] ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
        var root = $"{baseUrl}/api/auth/sso/{slug}";
        return ($"{root}/metadata", $"{root}/acs", $"{root}/metadata");
    }

    private static string WebBase(IConfiguration c) =>
        (c["App:WebBaseUrl"]
         ?? c["AllowedOrigins"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
         ?? "http://localhost:3100").TrimEnd('/');

    private static SamlConfigDto ToDto(HttpContext ctx, string slug, TenantSamlConfig? cfg)
    {
        var (spEntityId, acsUrl, metadataUrl) = SpUrls(ctx, slug);
        var loginUrl = metadataUrl.Replace("/metadata", "/login");
        return new SamlConfigDto(
            cfg?.Enabled ?? false,
            cfg?.IdpEntityId ?? "",
            cfg?.IdpSsoUrl ?? "",
            !string.IsNullOrWhiteSpace(cfg?.IdpCertificatePem),
            cfg?.EmailAttribute, cfg?.NameAttribute,
            cfg?.AllowJitProvisioning ?? true,
            spEntityId, acsUrl, metadataUrl, loginUrl);
    }

    /// <summary>A minimal principal for the provisioned/SSO user so AuditService can
    /// record the actor (it reads sub/email/name/role claims).</summary>
    private static ClaimsPrincipal PrincipalFor(User u) => new(new ClaimsIdentity(new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, u.Id.ToString()),
        new Claim(JwtRegisteredClaimNames.Email, u.Email),
        new Claim("name", u.Name),
        new Claim("role", u.Role.ToString()),
    }, "saml"));

    private static IResult Bad(string msg) => Results.Json(new { error = msg }, statusCode: 400);
    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static bool IsHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static bool CertParses(string pem)
    {
        try
        {
            var s = pem.Trim();
            using var _ = s.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal)
                ? System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(s)
                : new System.Security.Cryptography.X509Certificates.X509Certificate2(Convert.FromBase64String(s));
            return true;
        }
        catch { return false; }
    }
}
