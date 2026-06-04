using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Auth;

/// <summary>
/// Convenience accessors over the authenticated <see cref="ClaimsPrincipal"/>.
/// </summary>
public static class CurrentUser
{
    public static int Id(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : 0;

    public static string Email(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Email) ?? "";

    public static string Name(this ClaimsPrincipal user) =>
        user.FindFirstValue("name") ?? "";

    public static UserRole Role(this ClaimsPrincipal user) =>
        Enum.TryParse<UserRole>(user.FindFirstValue("role"), out var r) ? r : UserRole.TenantUser;

    /// <summary>TenantAdmin and SuperAdmin see every project in the tenant;
    /// a plain TenantUser only sees projects their teams are assigned to.</summary>
    public static bool IsAdmin(this ClaimsPrincipal user) =>
        user.Role() is UserRole.TenantAdmin or UserRole.SuperAdmin;
}
