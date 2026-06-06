using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ───────────────────────────────────────────────────────────────────────
public record GroupRefDto(int Id, string Code, string Name);
public record AdminUserDto(
    int Id, string Name, string Email, string Role, bool IsActive,
    DateTime CreatedAt, DateTime? LastLoginAt, List<GroupRefDto> Groups);
public record CreateUserInput(string? Name, string? Email, string? Password, string? Role, List<int>? GroupIds);
public record UpdateUserInput(string? Name, string? Email, string? Role, bool IsActive, List<int>? GroupIds);
public record ResetPasswordInput(string? Password);

public record ModuleDto(int Id, string Code, string Name);
public record GroupPermDto(int ModuleId, bool CanView, bool CanAdd, bool CanEdit, bool CanDelete);
public record AdminGroupDto(
    int Id, string Code, string Name, string? Description, bool IsBuiltIn,
    int MemberCount, List<GroupPermDto> Permissions);
public record CreateGroupInput(string? Code, string? Name, string? Description);
public record UpdateGroupInput(string? Name, string? Description);
public record GroupPermsInput(List<GroupPermDto>? Permissions);

/// <summary>
/// Tenant administration: manage users (accounts + roles + team membership) and
/// teams/groups (with their per-module permission grid). Every route is restricted
/// to a tenant admin; the tenant query filter scopes all reads/writes to the
/// caller's tenant. Lockout guards prevent an admin from removing the last admin or
/// disabling their own account mid-session.
/// </summary>
public static class UserManagementEndpoints
{
    private const int MinPasswordLength = 8;

    public static void MapUserManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/admin").RequireAuthorization();

        // Admin gate on every route in this group.
        grp.AddEndpointFilter(async (ctx, next) =>
        {
            var me = ctx.HttpContext.User;
            return me.IsAdmin()
                ? await next(ctx)
                : Results.Json(new { error = "Only a tenant admin can manage users and teams." }, statusCode: 403);
        });

        MapUsers(grp);
        MapGroups(grp);

        // ── Module catalog (for the group permission grid) ───────────────────
        grp.MapGet("/modules", async (AppDbContext db) =>
            Results.Ok(await db.Modules.OrderBy(m => m.SortOrder).ThenBy(m => m.Code)
                .Select(m => new ModuleDto(m.Id, m.Code, m.Name)).ToListAsync()));
    }

    // ── Users ────────────────────────────────────────────────────────────────
    private static void MapUsers(RouteGroupBuilder grp)
    {
        grp.MapGet("/users", async (AppDbContext db) =>
        {
            var users = await db.Users.OrderBy(u => u.Name).ToListAsync();
            var memberships = await (
                from ug in db.UserGroups
                join g in db.Groups on ug.GroupId equals g.Id
                select new { ug.UserId, g.Id, g.Code, g.Name }).ToListAsync();
            var byUser = memberships.GroupBy(m => m.UserId)
                .ToDictionary(g => g.Key, g => g.Select(x => new GroupRefDto(x.Id, x.Code, x.Name)).ToList());

            return Results.Ok(users.Select(u => new AdminUserDto(
                u.Id, u.Name, u.Email, u.Role.ToString(), u.IsActive, u.CreatedAt, u.LastLoginAt,
                byUser.TryGetValue(u.Id, out var gs) ? gs : new())).ToList());
        });

        grp.MapPost("/users", async (CreateUserInput i, ClaimsPrincipal me, AppDbContext db, AuditService audit, ITenantContext tc) =>
        {
            var name = (i.Name ?? "").Trim();
            var email = (i.Email ?? "").Trim().ToLowerInvariant();
            if (name.Length == 0) return Bad("Name is required.");
            if (!LooksLikeEmail(email)) return Bad("A valid email is required.");
            if ((i.Password ?? "").Length < MinPasswordLength) return Bad($"Password must be at least {MinPasswordLength} characters.");
            if (!TryParseTenantRole(i.Role, out var role)) return Bad("Role must be TenantUser or TenantAdmin.");
            if (await db.Users.AnyAsync(u => u.Email == email)) return Conflict("A user with that email already exists.");

            var groupIds = await ValidGroupIdsAsync(db, i.GroupIds);
            // User is not IHasTenant (TenantId is nullable for SuperAdmins), so it is NOT
            // auto-stamped on save — set the tenant explicitly or the row falls outside the filter.
            var user = new User { TenantId = tc.TenantId, Name = name, Email = email, Role = role, IsActive = true,
                                  PasswordHash = BCrypt.Net.BCrypt.HashPassword(i.Password) };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            foreach (var gid in groupIds) db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = gid });
            await db.SaveChangesAsync();
            await RefreshMemberCountsAsync(db, groupIds);

            await audit.LogAsync(me, "user.create", "User", user.Id.ToString(), $"{user.Email} ({user.Role})");
            return Results.Created($"/api/admin/users/{user.Id}", await ToUserDto(db, user.Id));
        });

        grp.MapPut("/users/{id:int}", async (int id, UpdateUserInput i, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();

            var name = (i.Name ?? "").Trim();
            var email = (i.Email ?? "").Trim().ToLowerInvariant();
            if (name.Length == 0) return Bad("Name is required.");
            if (!LooksLikeEmail(email)) return Bad("A valid email is required.");
            if (!TryParseTenantRole(i.Role, out var role)) return Bad("Role must be TenantUser or TenantAdmin.");
            if (await db.Users.AnyAsync(u => u.Email == email && u.Id != id)) return Conflict("A user with that email already exists.");

            var willBeAdmin = role == UserRole.TenantAdmin && i.IsActive;
            if (!willBeAdmin && await IsLastActiveAdmin(db, id))
                return Conflict("This is the last active admin — promote another admin first.");
            if (id == me.Id() && !i.IsActive)
                return Conflict("You cannot deactivate your own account.");

            // A role change or a deactivation must invalidate the user's outstanding
            // tokens immediately (privilege change / access revocation), so bump the
            // token-version stamp when either flips.
            if (user.Role != role || user.IsActive != i.IsActive) user.TokenVersion++;
            user.Name = name; user.Email = email; user.Role = role; user.IsActive = i.IsActive;

            // Replace team memberships wholesale.
            var groupIds = await ValidGroupIdsAsync(db, i.GroupIds);
            var existing = await db.UserGroups.Where(ug => ug.UserId == id).ToListAsync();
            var affected = existing.Select(e => e.GroupId).Union(groupIds).ToHashSet();
            db.UserGroups.RemoveRange(existing.Where(e => !groupIds.Contains(e.GroupId)));
            foreach (var gid in groupIds.Where(g => existing.All(e => e.GroupId != g)))
                db.UserGroups.Add(new UserGroup { UserId = id, GroupId = gid });
            await db.SaveChangesAsync();
            await RefreshMemberCountsAsync(db, affected);

            await audit.LogAsync(me, "user.update", "User", id.ToString(), $"{user.Email} ({user.Role}{(user.IsActive ? "" : ", inactive")})");
            return Results.Ok(await ToUserDto(db, id));
        });

        grp.MapPost("/users/{id:int}/reset-password", async (int id, ResetPasswordInput i, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();
            if ((i.Password ?? "").Length < MinPasswordLength) return Bad($"Password must be at least {MinPasswordLength} characters.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(i.Password);
            // A password reset must log out everyone holding an old token for this account.
            user.TokenVersion++;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "user.password-reset", "User", id.ToString(), user.Email);
            return Results.NoContent();
        });

        grp.MapDelete("/users/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();
            if (id == me.Id()) return Conflict("You cannot delete your own account.");
            if (user.Role == UserRole.TenantAdmin && user.IsActive && await IsLastActiveAdmin(db, id))
                return Conflict("This is the last active admin — promote another admin first.");

            var groupIds = await db.UserGroups.Where(ug => ug.UserId == id).Select(ug => ug.GroupId).ToListAsync();
            db.Users.Remove(user);   // UserGroups cascade
            await db.SaveChangesAsync();
            await RefreshMemberCountsAsync(db, groupIds);
            await audit.LogAsync(me, "user.delete", "User", id.ToString(), user.Email);
            return Results.NoContent();
        });
    }

    // ── Groups / teams ─────────────────────────────────────────────────────────
    private static void MapGroups(RouteGroupBuilder grp)
    {
        grp.MapGet("/groups", async (AppDbContext db) =>
        {
            var groups = await db.Groups.OrderBy(g => g.Name).ToListAsync();
            var perms = await db.GroupModules.ToListAsync();
            var counts = await db.UserGroups.GroupBy(ug => ug.GroupId)
                .Select(g => new { GroupId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.GroupId, x => x.Count);

            return Results.Ok(groups.Select(g => new AdminGroupDto(
                g.Id, g.Code, g.Name, g.Description, g.IsBuiltIn,
                counts.TryGetValue(g.Id, out var c) ? c : 0,
                perms.Where(p => p.GroupId == g.Id)
                     .Select(p => new GroupPermDto(p.ModuleId, p.CanView, p.CanAdd, p.CanEdit, p.CanDelete)).ToList()
            )).ToList());
        });

        grp.MapPost("/groups", async (CreateGroupInput i, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            var code = (i.Code ?? "").Trim().ToUpperInvariant();
            var name = (i.Name ?? "").Trim();
            if (code.Length == 0) return Bad("Code is required.");
            if (name.Length == 0) return Bad("Name is required.");
            if (await db.Groups.AnyAsync(g => g.Code == code)) return Conflict("A team with that code already exists.");

            var g = new Group { Code = code, Name = name, Description = Trim(i.Description), IsBuiltIn = false };
            db.Groups.Add(g);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "group.create", "Group", g.Id.ToString(), $"{g.Code} — {g.Name}");
            return Results.Created($"/api/admin/groups/{g.Id}", await ToGroupDto(db, g.Id));
        });

        grp.MapPut("/groups/{id:int}", async (int id, UpdateGroupInput i, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id);
            if (g is null) return Results.NotFound();
            var name = (i.Name ?? "").Trim();
            if (name.Length == 0) return Bad("Name is required.");
            g.Name = name; g.Description = Trim(i.Description); g.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "group.update", "Group", id.ToString(), $"{g.Code} — {g.Name}");
            return Results.Ok(await ToGroupDto(db, id));
        });

        grp.MapPut("/groups/{id:int}/permissions", async (int id, GroupPermsInput i, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id);
            if (g is null) return Results.NotFound();

            var validModuleIds = await db.Modules.Select(m => m.Id).ToListAsync();
            var incoming = (i.Permissions ?? new())
                .Where(p => validModuleIds.Contains(p.ModuleId))
                // A row with no rights is meaningless — drop it (absence = no access).
                .Where(p => p.CanView || p.CanAdd || p.CanEdit || p.CanDelete)
                // CanAdd/Edit/Delete imply visibility.
                .Select(p => p with { CanView = true })
                .GroupBy(p => p.ModuleId).Select(grp2 => grp2.First())
                .ToList();

            var existing = await db.GroupModules.Where(gm => gm.GroupId == id).ToListAsync();
            db.GroupModules.RemoveRange(existing);
            await db.SaveChangesAsync();
            foreach (var p in incoming)
                db.GroupModules.Add(new GroupModule
                {
                    GroupId = id, ModuleId = p.ModuleId,
                    CanView = p.CanView, CanAdd = p.CanAdd, CanEdit = p.CanEdit, CanDelete = p.CanDelete,
                });
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "group.permissions", "Group", id.ToString(), $"{g.Code}: {incoming.Count} module(s)");
            return Results.Ok(await ToGroupDto(db, id));
        });

        grp.MapDelete("/groups/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id);
            if (g is null) return Results.NotFound();
            if (g.IsBuiltIn) return Conflict("Built-in teams cannot be deleted.");

            var projects = await db.ProjectTeams.CountAsync(pt => pt.GroupId == id);
            if (projects > 0) return Conflict($"This team is assigned to {projects} project(s). Unassign it first.");

            db.Groups.Remove(g);   // UserGroups + GroupModules cascade
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "group.delete", "Group", id.ToString(), $"{g.Code} — {g.Name}");
            return Results.NoContent();
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static IResult Bad(string msg) => Results.Json(new { error = msg }, statusCode: 400);
    private static IResult Conflict(string msg) => Results.Json(new { error = msg }, statusCode: 409);
    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static bool LooksLikeEmail(string e) => e.Length >= 3 && e.Contains('@') && !e.StartsWith('@') && !e.EndsWith('@');

    private static bool TryParseTenantRole(string? raw, out UserRole role)
    {
        role = UserRole.TenantUser;
        if (!Enum.TryParse<UserRole>(raw, ignoreCase: true, out var r)) return false;
        if (r is not (UserRole.TenantUser or UserRole.TenantAdmin)) return false;   // SuperAdmin not assignable here
        role = r;
        return true;
    }

    /// <summary>True if <paramref name="excludingUserId"/> is the only active TenantAdmin.</summary>
    private static async Task<bool> IsLastActiveAdmin(AppDbContext db, int excludingUserId) =>
        !await db.Users.AnyAsync(u => u.Id != excludingUserId && u.IsActive && u.Role == UserRole.TenantAdmin);

    private static async Task<List<int>> ValidGroupIdsAsync(AppDbContext db, List<int>? requested)
    {
        if (requested is null || requested.Count == 0) return new();
        var distinct = requested.Distinct().ToList();
        return await db.Groups.Where(g => distinct.Contains(g.Id)).Select(g => g.Id).ToListAsync();
    }

    /// <summary>Keep the denormalised Group.MemberCount in sync after membership changes.</summary>
    private static async Task RefreshMemberCountsAsync(AppDbContext db, IEnumerable<int> groupIds)
    {
        var ids = groupIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var counts = await db.UserGroups.Where(ug => ids.Contains(ug.GroupId))
            .GroupBy(ug => ug.GroupId).Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count);
        var groups = await db.Groups.Where(g => ids.Contains(g.Id)).ToListAsync();
        foreach (var g in groups) g.MemberCount = counts.TryGetValue(g.Id, out var c) ? c : 0;
        await db.SaveChangesAsync();
    }

    private static async Task<AdminUserDto> ToUserDto(AppDbContext db, int id)
    {
        var u = await db.Users.FirstAsync(x => x.Id == id);
        var groups = await (from ug in db.UserGroups.Where(x => x.UserId == id)
                            join g in db.Groups on ug.GroupId equals g.Id
                            select new GroupRefDto(g.Id, g.Code, g.Name)).ToListAsync();
        return new AdminUserDto(u.Id, u.Name, u.Email, u.Role.ToString(), u.IsActive, u.CreatedAt, u.LastLoginAt, groups);
    }

    private static async Task<AdminGroupDto> ToGroupDto(AppDbContext db, int id)
    {
        var g = await db.Groups.FirstAsync(x => x.Id == id);
        var perms = await db.GroupModules.Where(gm => gm.GroupId == id)
            .Select(p => new GroupPermDto(p.ModuleId, p.CanView, p.CanAdd, p.CanEdit, p.CanDelete)).ToListAsync();
        var count = await db.UserGroups.CountAsync(ug => ug.GroupId == id);
        return new AdminGroupDto(g.Id, g.Code, g.Name, g.Description, g.IsBuiltIn, count, perms);
    }
}
