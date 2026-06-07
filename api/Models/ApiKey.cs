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

    public DateTime  CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    /// <summary>Optional expiry; null = never expires.</summary>
    public DateTime? ExpiresAt  { get; set; }
    /// <summary>Set when revoked; a revoked key authenticates nothing.</summary>
    public DateTime? RevokedAt  { get; set; }
}
