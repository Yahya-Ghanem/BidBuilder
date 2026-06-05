using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Auth;

/// <summary>
/// Issues signed JWTs for authenticated users. The token carries the claims the
/// rest of the pipeline relies on: the user id, role, and crucially the
/// <c>tenant_slug</c> that <see cref="Tenancy.TenantResolutionMiddleware"/> reads
/// to scope every subsequent request.
/// </summary>
public class JwtService(IConfiguration cfg)
{
    public const string TenantSlugClaim = "tenant_slug";
    public const string TenantIdClaim   = "tenant_id";

    /// <summary>
    /// Issue a platform-level token for a SuperAdmin. These users have no tenant, so
    /// the token carries an empty tenant_slug; <see cref="Tenancy.TenantResolutionMiddleware"/>
    /// recognises the SuperAdmin role and lets the request through without resolving a
    /// tenant (it may only reach the /api/platform endpoints).
    /// </summary>
    public (string Token, DateTime ExpiresAt) IssuePlatform(User user) => Issue(user, "");

    public (string Token, DateTime ExpiresAt) Issue(User user, string tenantSlug)
    {
        var key      = cfg["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey not configured");
        var issuer   = cfg["Jwt:Issuer"]   ?? "bidbuilder";
        var audience = cfg["Jwt:Audience"] ?? "bidbuilder";

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);

        var expires = DateTime.UtcNow.AddHours(8);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.Name),
            new("role", user.Role.ToString()),
            new(TenantSlugClaim, tenantSlug),
            new(TenantIdClaim, user.TenantId?.ToString() ?? ""),
        };

        var token = new JwtSecurityToken(
            issuer: issuer, audience: audience, claims: claims,
            notBefore: DateTime.UtcNow, expires: expires, signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
