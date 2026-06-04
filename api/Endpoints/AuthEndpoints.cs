using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, DateTime ExpiresAt, UserDto User);
public record UserDto(int Id, string Name, string Email, string Role);

public record ModulePermissionDto(string Code, string Name, bool CanView, bool CanAdd, bool CanEdit, bool CanDelete);
public record MePermissionsResponse(string Role, bool IsAdmin, List<ModulePermissionDto> Modules);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth");

        // POST /api/auth/login — tenant comes from X-Tenant-Id header (or, once
        // logged in, the JWT). Returns a JWT carrying tenant_slug + role.
        grp.MapPost("/login", async (LoginRequest req, AppDbContext db, ITenantContext tenant, JwtService jwt) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == req.Email.Trim().ToLower());
            if (user is null || !user.IsActive)
                return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);

            if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);

            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var (token, expires) = jwt.Issue(user, tenant.TenantSlug);
            return Results.Ok(new LoginResponse(
                token, expires,
                new UserDto(user.Id, user.Name, user.Email, user.Role.ToString())));
        })
        .AllowAnonymous();

        // GET /api/auth/permissions — the caller's effective module permissions, so
        // the UI can hide affordances the API would reject anyway. Admins get every
        // seeded module with full rights; others get the OR of their teams' GroupModule
        // flags. The API still enforces on every mutation — this is convenience only.
        grp.MapGet("/permissions", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var modules = await db.Modules.OrderBy(m => m.Code)
                .Select(m => new { m.Id, m.Code, m.Name })
                .ToListAsync();

            if (me.IsAdmin())
            {
                var all = modules
                    .Select(m => new ModulePermissionDto(m.Code, m.Name, true, true, true, true))
                    .ToList();
                return Results.Ok(new MePermissionsResponse(me.Role().ToString(), true, all));
            }

            var uid = me.Id();
            var flags = await (
                from ug in db.UserGroups.Where(x => x.UserId == uid)
                join gm in db.GroupModules on ug.GroupId equals gm.GroupId
                select gm).ToListAsync();

            var byModule = flags
                .GroupBy(f => f.ModuleId)
                .ToDictionary(g => g.Key, g => (
                    View:   g.Any(x => x.CanView),
                    Add:    g.Any(x => x.CanAdd),
                    Edit:   g.Any(x => x.CanEdit),
                    Delete: g.Any(x => x.CanDelete)));

            var list = modules.Select(m =>
            {
                byModule.TryGetValue(m.Id, out var f);
                return new ModulePermissionDto(m.Code, m.Name, f.View, f.Add, f.Edit, f.Delete);
            }).ToList();

            return Results.Ok(new MePermissionsResponse(me.Role().ToString(), false, list));
        })
        .RequireAuthorization();
    }
}
