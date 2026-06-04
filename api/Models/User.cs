namespace BidBuilder.Api.Models;

/// <summary>
/// Tiered roles. SuperAdmin is platform-wide (not tenant-scoped — TenantId null).
/// TenantAdmin and TenantUser are scoped to a tenant via <see cref="User.TenantId"/>.
/// </summary>
public enum UserRole
{
    TenantUser  = 0,
    TenantAdmin = 1,
    SuperAdmin  = 2,
}

/// <summary>
/// A login-able account. SuperAdmins exist outside any tenant (TenantId null).
/// Tenant users belong to exactly one tenant and reach projects through the
/// teams (<see cref="Group"/>) they are members of.
/// </summary>
public class User
{
    public int      Id           { get; set; }
    public Guid?    TenantId     { get; set; }          // null for SuperAdmin
    public string   Email        { get; set; } = "";    // unique within tenant
    public string   PasswordHash { get; set; } = "";    // bcrypt; never returned by API
    public string   Name         { get; set; } = "";
    public UserRole Role         { get; set; } = UserRole.TenantUser;
    public bool     IsActive     { get; set; } = true;

    public DateTime  CreatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt  { get; set; }

    // ── 2FA (TOTP) ───────────────────────────────────────────────────────────
    public byte[]?   TotpSecret            { get; set; }   // null = 2FA disabled
    public DateTime? TotpEnabledAt         { get; set; }
    public string?   TotpRecoveryCodesJson { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────────
    public ICollection<UserGroup> UserGroups { get; set; } = new List<UserGroup>();
}
