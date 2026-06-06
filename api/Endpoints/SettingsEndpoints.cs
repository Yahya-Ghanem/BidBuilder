using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

public record SettingsDto(
    string CompanyName, string? Website, string? ContactEmail, string? Phone,
    string? Address, string? City, string? Country, string Timezone,
    string BaseCurrency, decimal DefaultOverheadPct, decimal DefaultProfitPct, decimal DefaultContingencyPct,
    decimal DefaultTaxRatePct, bool HasLogo,
    /// <summary>20.2 — sign-offs required before a Draft can be Published.
    /// Zero = no approval workflow (legacy behavior).</summary>
    int RequiredApprovalsToPublish,
    /// <summary>20.11 — the tenant's vanity host, or null if none is registered.</summary>
    string? CustomDomain);

/// <summary>20.11 — set (non-empty) or clear (null/empty) the tenant's custom domain.</summary>
public record CustomDomainInput(string? Domain);

public record SettingsInput(
    string? Website, string? ContactEmail, string? Phone, string? Address, string? City, string? Country,
    string? Timezone, string? BaseCurrency, decimal DefaultOverheadPct, decimal DefaultProfitPct, decimal DefaultContingencyPct,
    decimal DefaultTaxRatePct,
    /// <summary>20.2 — null leaves the existing value alone (back-compat).</summary>
    int? RequiredApprovalsToPublish);

public record CurrencyRateDto(string Code, decimal RateToBase, DateTime UpdatedAt);
public record CurrencyRatesDto(string BaseCurrency, List<CurrencyRateDto> Rates);
public record CurrencyRateInput(decimal RateToBase);

/// <summary>
/// Tenant company profile + estimating defaults (1:1 TenantSettings). Any signed-in
/// user may read; only a tenant admin may edit. The company profile also brands the
/// exported bid documents.
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/settings").RequireAuthorization();

        grp.MapGet("/", async (AppDbContext db, ITenantContext tc) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tc.TenantId);
            var s = await db.TenantSettings.FirstOrDefaultAsync();
            return Results.Ok(ToDto(tenant?.Name ?? "BidBuilder", s, tenant?.CustomDomain));
        });

        grp.MapPut("/", async (SettingsInput i, ClaimsPrincipal me, AppDbContext db, ITenantContext tc) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can edit settings" }, statusCode: 403);

            // Validate before any write — a bad base currency would otherwise hit the
            // varchar(3) column and throw a 500 instead of a clean 400, and negative
            // default percentages would silently corrupt every new estimate's markups.
            string? baseCurrency = null;
            if (!string.IsNullOrWhiteSpace(i.BaseCurrency))
            {
                baseCurrency = i.BaseCurrency!.Trim().ToUpperInvariant();
                if (baseCurrency.Length != 3 || !baseCurrency.All(char.IsLetter))
                    return Bad("Base currency must be a 3-letter ISO-4217 code.");
            }
            if (i.DefaultOverheadPct < 0 || i.DefaultProfitPct < 0 || i.DefaultContingencyPct < 0)
                return Bad("Default percentages cannot be negative.");
            if (i.DefaultTaxRatePct < 0 || i.DefaultTaxRatePct > 100)
                return Bad("Default tax rate must be between 0 and 100.");
            if (i.RequiredApprovalsToPublish is { } req && req < 0)
                return Bad("Required approvals cannot be negative.");

            var s = await db.TenantSettings.FirstOrDefaultAsync();
            if (s is null) { s = new TenantSettings(); db.TenantSettings.Add(s); }   // TenantId auto-stamped on save

            s.Website = Trim(i.Website); s.ContactEmail = Trim(i.ContactEmail); s.Phone = Trim(i.Phone);
            s.Address = Trim(i.Address); s.City = Trim(i.City); s.Country = Trim(i.Country);
            if (!string.IsNullOrWhiteSpace(i.Timezone)) s.Timezone = i.Timezone!.Trim();
            if (baseCurrency is not null) s.BaseCurrency = baseCurrency;
            s.DefaultOverheadPct = i.DefaultOverheadPct;
            s.DefaultProfitPct = i.DefaultProfitPct;
            s.DefaultContingencyPct = i.DefaultContingencyPct;
            s.DefaultTaxRatePct = i.DefaultTaxRatePct;
            if (i.RequiredApprovalsToPublish is { } reqAps) s.RequiredApprovalsToPublish = reqAps;
            s.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tc.TenantId);
            return Results.Ok(ToDto(tenant?.Name ?? "BidBuilder", s, tenant?.CustomDomain));
        });

        // ── Custom domain (20.11) ────────────────────────────────────────────
        // Register (or clear) the vanity host the workspace is reached at. Tenant
        // admin only. Stored lowercase; must be globally unique (409 otherwise) so two
        // workspaces can't claim the same host. The middleware resolves a request with
        // no token/header to this tenant when the request Host matches. DNS + TLS for
        // the host are an ops concern (see docs/DEPLOYMENT.md).
        grp.MapPut("/custom-domain", async (CustomDomainInput i, ClaimsPrincipal me, AppDbContext db, ITenantContext tc, AuditService audit) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can change the custom domain" }, statusCode: 403);

            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tc.TenantId);
            if (tenant is null) return Results.NotFound(new { error = "Tenant not found" });

            var raw = i.Domain?.Trim().ToLowerInvariant();
            string? domain = string.IsNullOrEmpty(raw) ? null : raw;

            if (domain is not null)
            {
                // Strip an accidental scheme/path so "https://bids.acme.com/" is accepted.
                domain = domain.Replace("https://", "").Replace("http://", "").TrimEnd('/');
                if (!IsValidDomain(domain))
                    return Bad("Enter a valid hostname, e.g. bids.acme.com.");
                // Unique across all tenants (the query filter does NOT apply to Tenants).
                if (await db.Tenants.AnyAsync(t => t.CustomDomain == domain && t.Id != tenant.Id))
                    return Results.Conflict(new { error = "That domain is already in use by another workspace." });
            }

            var old = tenant.CustomDomain;
            tenant.CustomDomain = domain;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "tenant.custom-domain", "Tenant", tenant.Slug,
                domain is null ? $"cleared (was {old ?? "none"})" : $"set to {domain}");

            var s = await db.TenantSettings.FirstOrDefaultAsync();
            return Results.Ok(ToDto(tenant.Name, s, tenant.CustomDomain));
        });

        // ── Company logo ─────────────────────────────────────────────────────
        // Stored as bytes on TenantSettings; branded onto exported bid documents.
        grp.MapPost("/logo", async (IFormFile file, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can change the logo" }, statusCode: 403);

            var ct = file.ContentType?.ToLowerInvariant();
            if (ct != "image/png" && ct != "image/jpeg")
                return Results.Json(new { error = "Logo must be a PNG or JPEG image." }, statusCode: 400);
            if (file.Length <= 0 || file.Length > 1_000_000)
                return Results.Json(new { error = "Logo must be a non-empty file under 1 MB." }, statusCode: 400);

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();

            // The content-type header is client-controlled — verify the actual bytes are
            // a real PNG/JPEG so a mislabeled/garbage file can't be stored and then 500
            // every branded export when the image decoder chokes on it.
            if (!LooksLikeImage(bytes))
                return Results.Json(new { error = "Logo must be a valid PNG or JPEG image." }, statusCode: 400);

            var s = await db.TenantSettings.FirstOrDefaultAsync();
            if (s is null) { s = new TenantSettings(); db.TenantSettings.Add(s); }
            s.LogoBytes = bytes;
            s.LogoContentType = ct;
            s.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { hasLogo = true });
        }).DisableAntiforgery();

        grp.MapGet("/logo", async (AppDbContext db) =>
        {
            var s = await db.TenantSettings.FirstOrDefaultAsync();
            return s?.LogoBytes is { Length: > 0 } bytes
                ? Results.File(bytes, s.LogoContentType ?? "image/png")
                : Results.NotFound();
        });

        grp.MapDelete("/logo", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can change the logo" }, statusCode: 403);

            var s = await db.TenantSettings.FirstOrDefaultAsync();
            if (s is not null)
            {
                s.LogoBytes = null; s.LogoContentType = null; s.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { hasLogo = false });
        });

        // ── Currency rates (manual FX) ───────────────────────────────────────
        // Any signed-in user may read (estimators pick a presentation currency);
        // only a tenant admin may edit. Rates are units of Code per 1 base unit.
        grp.MapGet("/currencies", async (AppDbContext db) =>
        {
            var baseC = await db.TenantSettings.Select(s => s.BaseCurrency).FirstOrDefaultAsync() ?? "AED";
            var rates = await db.CurrencyRates.OrderBy(r => r.Code)
                .Select(r => new CurrencyRateDto(r.Code, r.RateToBase, r.UpdatedAt)).ToListAsync();
            return Results.Ok(new CurrencyRatesDto(baseC, rates));
        });

        grp.MapPut("/currencies/{code}", async (string code, CurrencyRateInput i, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can edit currency rates" }, statusCode: 403);

            code = (code ?? "").Trim().ToUpperInvariant();
            if (code.Length != 3) return Results.Json(new { error = "Currency code must be 3 letters (ISO-4217)." }, statusCode: 400);
            var baseC = await db.TenantSettings.Select(s => s.BaseCurrency).FirstOrDefaultAsync() ?? "AED";
            if (string.Equals(code, baseC, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = $"{code} is the base currency — its rate is always 1." }, statusCode: 400);
            if (i.RateToBase <= 0) return Results.Json(new { error = "Rate must be greater than zero." }, statusCode: 400);

            var r = await db.CurrencyRates.FirstOrDefaultAsync(x => x.Code == code);
            if (r is null) { r = new CurrencyRate { Code = code }; db.CurrencyRates.Add(r); }
            r.RateToBase = i.RateToBase; r.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new CurrencyRateDto(r.Code, r.RateToBase, r.UpdatedAt));
        });

        grp.MapDelete("/currencies/{code}", async (string code, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can edit currency rates" }, statusCode: 403);
            code = (code ?? "").Trim().ToUpperInvariant();
            var r = await db.CurrencyRates.FirstOrDefaultAsync(x => x.Code == code);
            if (r is not null) { db.CurrencyRates.Remove(r); await db.SaveChangesAsync(); }
            return Results.NoContent();
        });

        // FX refresh (18.3) — touches every rate's UpdatedAt and writes an audit row,
        // proving "FX rates were reviewed on date X by user Y". This is the manual-confirm
        // surrogate for a scheduled provider pull (which would add a background-job
        // dependency — deferred to 18.4 to avoid two infra changes in one PR). Tenant
        // admin only.
        grp.MapPost("/currencies/refresh", async (ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can refresh currency rates" }, statusCode: 403);
            var rates = await db.CurrencyRates.ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var r in rates) r.UpdatedAt = now;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "fx.refresh", "CurrencyRate", null,
                $"reviewed {rates.Count} currency rate(s)");
            return Results.Ok(new { reviewed = rates.Count, at = now });
        });
    }

    private static IResult Bad(string msg) => Results.Json(new { error = msg }, statusCode: 400);

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>True if the bytes start with a PNG or JPEG magic-number signature.</summary>
    private static bool LooksLikeImage(byte[] b)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return true;
        // JPEG: FF D8 FF
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
        return false;
    }

    private static SettingsDto ToDto(string companyName, TenantSettings? s, string? customDomain = null) => new(
        companyName, s?.Website, s?.ContactEmail, s?.Phone, s?.Address, s?.City, s?.Country,
        s?.Timezone ?? "UTC", s?.BaseCurrency ?? "AED",
        s?.DefaultOverheadPct ?? 0, s?.DefaultProfitPct ?? 0, s?.DefaultContingencyPct ?? 0,
        s?.DefaultTaxRatePct ?? 0,
        s?.LogoBytes is { Length: > 0 },
        s?.RequiredApprovalsToPublish ?? 0,
        customDomain);

    /// <summary>Validate a hostname for use as a custom domain. Lowercased, 1–253 chars,
    /// dot-separated DNS labels (letters/digits/hyphens, no leading/trailing hyphen),
    /// at least two labels (so "localhost" or a bare word is rejected).</summary>
    public static bool IsValidDomain(string host)
    {
        if (host.Length is 0 or > 253) return false;
        var labels = host.Split('.');
        if (labels.Length < 2) return false;
        foreach (var l in labels)
        {
            if (l.Length is 0 or > 63) return false;
            if (l[0] == '-' || l[^1] == '-') return false;
            foreach (var ch in l)
                if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-')) return false;
        }
        // The last label (TLD) must be alphabetic.
        return labels[^1].All(char.IsAsciiLetter);
    }
}
