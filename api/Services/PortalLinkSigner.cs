using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;

namespace BidBuilder.Api.Services;

/// <summary>
/// 23.3 — Signs and validates anonymous-portal URLs (today the subcontractor-quote
/// portal at <c>/portal/{token}?exp=&amp;sig=</c>). Each tenant carries its own random
/// HMAC key (<see cref="Models.Tenant.PortalSigningKey"/>), so a leak in one tenant's
/// links never compromises another's.
///
/// Signature shape:
///   sig = HMAC-SHA256(tenant.PortalSigningKey, $"{token}.{exp}")
/// where <c>token</c> is the existing per-quote token and <c>exp</c> is a Unix-seconds
/// expiry. Both are passed as URL query parameters so the path itself stays unchanged.
///
/// Back-compat: when <c>PortalLinks:AllowUnsigned</c> is true (the default for one
/// release cycle), a URL without sig/exp is still honored — existing tokens keep working
/// while we cut over. Set the flag false in a future release to enforce sig presence.
/// </summary>
public class PortalLinkSigner(AppDbContext db, IConfiguration config)
{
    /// <summary>Lifetime applied when a caller didn't pick one — matches the typical
    /// RFQ turnaround. Clamped on the caller path to [1d, 365d].</summary>
    public const int DefaultValidDays = 7;

    /// <summary>Are unsigned URLs (no <c>sig</c>) still accepted? Default true for 23.3
    /// release; flip to false in a later release to enforce signing.</summary>
    public bool AllowUnsigned => config.GetValue("PortalLinks:AllowUnsigned", true);

    /// <summary>Mint a signed portal path for an existing quote token. <paramref name="validDays"/>
    /// is clamped to <c>[1, 365]</c> (defaults to <see cref="DefaultValidDays"/>). The signing
    /// key is auto-rotated in if the tenant didn't have one yet (older tenants pre-23.3).</summary>
    public async Task<string> SignAsync(Guid tenantId, string token, int? validDays, CancellationToken ct = default)
    {
        var key = await GetOrCreateKeyAsync(tenantId, ct);
        var days = Math.Clamp(validDays ?? DefaultValidDays, 1, 365);
        var exp  = ToUnixSeconds(DateTime.UtcNow.AddDays(days));
        var sig  = Hmac(key, $"{token}.{exp}");
        return $"/portal/{Uri.EscapeDataString(token)}?exp={exp}&sig={sig}";
    }

    /// <summary>Verdict on a presented <c>(token, exp, sig)</c> triple. <see cref="Reason"/>
    /// is empty on success or one of: "missing", "expired", "invalid" so callers can
    /// surface a precise 401 message.</summary>
    public sealed record VerifyResult(bool Ok, string Reason);

    /// <summary>Validate a portal URL's signature + freshness against the tenant's key.
    /// When <see cref="AllowUnsigned"/> is true a request with no <c>sig</c> AND no <c>exp</c>
    /// passes (back-compat). A partially signed URL (sig OR exp present alone) always fails.</summary>
    public async Task<VerifyResult> VerifyAsync(Guid tenantId, string token, string? expRaw, string? sig, CancellationToken ct = default)
    {
        var hasExp = !string.IsNullOrEmpty(expRaw);
        var hasSig = !string.IsNullOrEmpty(sig);

        if (!hasExp && !hasSig)
            return AllowUnsigned ? new(true, "") : new(false, "missing");
        if (hasExp ^ hasSig)
            return new(false, "missing");

        if (!long.TryParse(expRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp))
            return new(false, "invalid");
        if (exp < ToUnixSeconds(DateTime.UtcNow))
            return new(false, "expired");

        var key = await GetOrCreateKeyAsync(tenantId, ct);
        var expected = Hmac(key, $"{token}.{exp}");
        // Constant-time compare so callers can't time the response to fish for the right sig.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig!)))
            return new(false, "invalid");

        return new(true, "");
    }

    // ── internals ────────────────────────────────────────────────────────────────

    private async Task<byte[]> GetOrCreateKeyAsync(Guid tenantId, CancellationToken ct)
    {
        var t = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException($"Unknown tenant {tenantId}");
        if (string.IsNullOrEmpty(t.PortalSigningKey))
        {
            t.PortalSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await db.SaveChangesAsync(ct);
        }
        return Convert.FromBase64String(t.PortalSigningKey);
    }

    private static string Hmac(byte[] key, string payload)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        // URL-safe base64 (no padding) so the value drops cleanly into a query string.
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
