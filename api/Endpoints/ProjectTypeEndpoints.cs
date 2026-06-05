using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Endpoints;

public record ProjectTypeDto(int Id, string Name, int SortOrder, bool IsActive, bool Builtin);
public record ProjectTypeInput(string Name, int SortOrder, bool IsActive);

/// <summary>
/// Tenant catalog of project types (Civil, Mechanical, Electrical…) offered when
/// creating or editing a project. Any signed-in user may read; anyone who can add a
/// project may add a new type on the fly; only a tenant admin may edit or delete
/// catalog entries. Built-in types cannot be deleted (deactivate them instead).
/// </summary>
public static class ProjectTypeEndpoints
{
    public static void MapProjectTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/project-types").RequireAuthorization();

        grp.MapGet("/", async (AppDbContext db) =>
            Results.Ok(await db.ProjectTypes.OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
                .Select(a => new ProjectTypeDto(a.Id, a.Name, a.SortOrder, a.IsActive, a.Builtin))
                .ToListAsync()));

        // Adding to the catalog needs the projects Add permission; admins bypass.
        grp.MapPost("/", async (ProjectTypeInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            if (!await perm.CanAsync(me, "projects", ModuleAction.Add))
                return Results.Json(new { error = "Missing 'Add' permission on projects" }, statusCode: 403);
            var name = (i.Name ?? "").Trim();
            if (name.Length == 0) return Results.Json(new { error = "Name is required." }, statusCode: 400);
            if (await db.ProjectTypes.AnyAsync(a => a.Name.ToLower() == name.ToLower()))
                return Results.Json(new { error = $"A project type '{name}' already exists." }, statusCode: 409);

            var a = new ProjectType { Name = name, SortOrder = i.SortOrder, IsActive = true, Builtin = false };
            db.ProjectTypes.Add(a);
            await db.SaveChangesAsync();
            return Results.Created($"/api/project-types/{a.Id}", ToDto(a));
        });

        grp.MapPut("/{id:int}", async (int id, ProjectTypeInput i, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var a = await db.ProjectTypes.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            var name = (i.Name ?? "").Trim();
            if (name.Length == 0) return Results.Json(new { error = "Name is required." }, statusCode: 400);
            if (await db.ProjectTypes.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower()))
                return Results.Json(new { error = $"A project type '{name}' already exists." }, statusCode: 409);

            if (!a.Builtin) a.Name = name;     // built-in names are fixed; only active toggles
            a.SortOrder = i.SortOrder;
            a.IsActive = i.IsActive;
            a.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(a));
        });

        grp.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var a = await db.ProjectTypes.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            if (a.Builtin) return Results.Json(new { error = "Built-in project types cannot be deleted (deactivate it instead)." }, statusCode: 409);
            if (await db.Projects.AnyAsync(p => p.ProjectTypeId == id))
                return Results.Json(new { error = "This type is used by one or more projects." }, statusCode: 409);
            db.ProjectTypes.Remove(a);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static IResult Forbid() => Results.Json(new { error = "Only a tenant admin can manage project types" }, statusCode: 403);
    private static ProjectTypeDto ToDto(ProjectType a) => new(a.Id, a.Name, a.SortOrder, a.IsActive, a.Builtin);
}
