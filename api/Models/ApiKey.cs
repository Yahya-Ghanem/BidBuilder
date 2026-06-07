namespace BidBuilder.Api.Models;

/// <summary>
/// 21.2 — A tenant-scoped programmatic credential. An API key authenticates a
/// headless caller (CI, an integration, a script) AS the user that created it, so
/// it inherits that user's role and team permissions and is automatically disabled
/// if the user is deactivated. Presented in the <c>X-Api-Key</c> header.
///
/// The secret itself is NEVER stored — only a SHA-256 hash (the secret is 256 bits
/// of CSPRNG entropy, so an unsalted hash is not brute-forceable). A short,
/// non-secret <see cref="Prefix"/> is kept so the owner can recognise a key in the
/// list without ever seeing the full value again.
/// </summary>
public class ApiKey : IHasTenant
{
    public int  Id       { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The user the key acts as (its creator). The key inherits this user's
    /// role + team permissions; deactivating the user disables the key.</summary>
    public int UserId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Non-secret leading chars of the key (e.g. <c>bbk_a1B2c3D4</c>) shown in
    /// the management list so a key is identifiable after creation.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>SHA-256 (lowercase hex) of the full secret. Unique. The secret is never persisted.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>22.2 — Comma-separated grants that gate what the key may do, on top of its
    /// owner's RBAC permissions. <c>read</c> permits safe methods (GET/HEAD/OPTIONS);
    /// <c>write</c> permits mutations (POST/PUT/PATCH/DELETE). A request whose method class
    /// isn't granted is rejected 403 by <see cref="Auth.ApiKeyGuard"/>. Pre-22.2 keys are
    /// backfilled to <c>read,write</c> (full access) so they keep working unchanged.</summary>
    public string Scopes { get; set; } = "read,write";

    /// <summary>22.2 — Optional throughput cap: max requests per minute for this key
    /// (fixed window). Null = unlimited. Enforced in-process by <see cref="Auth.ApiKeyRateLimiter"/>
    /// (429 + Retry-After on breach).</summary>
    public int? RateLimitPerMinute { get; set; }

    /// <summary>23.2 — Optional comma-separated list of CIDR ranges (IPv4 and/or IPv6) the key
    /// may be presented from. Null or empty = any IP. A bare address is treated as a /32 (or /128)
    /// host route. Enforced after the rate-limit and before the scope check in
    /// <see cref="Auth.ApiKeyGuard"/>: a request from outside the list is rejected 403 with a
    /// distinct "IP not allowed" reason. CIDR matching is byte-wise, so IPv6 mapped-IPv4
    /// addresses are normalized to their IPv4 form before comparison.</summary>
    public string? IpAllowlist { get; set; }

    public DateTime  CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    /// <summary>Optional expiry; null = never expires.</summary>
    public DateTime? ExpiresAt  { get; set; }
    /// <summary>Set when revoked; a revoked key authenticates nothing.</summary>
    public DateTime? RevokedAt  { get; set; }
}
