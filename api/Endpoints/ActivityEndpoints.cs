using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Endpoints;

public record ActivityTypeDto(int Id, string Name, int SortOrder, bool IsActive, bool Builtin);
public record ActivityTypeInput(string Name, int SortOrder, bool IsActive);

/// <summary>
/// Tenant catalog of construction activities offered when adding an activity under a
/// unit. Any signed-in user may read; anyone who can add BOQ content may add a new
/// activity on the fly (so estimators aren't blocked); only a tenant admin may edit
/// or delete catalog entries. Built-in activities cannot be deleted (deactivate them).
/// </summary>
public static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/activities").RequireAuthorization();

        grp.MapGet("/", async (AppDbContext db) =>
            Results.Ok(await db.ActivityTypes.OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
                .Select(a => new ActivityTypeDto(a.Id, a.Name, a.SortOrder, a.IsActive, a.Builtin))
                .ToListAsync()));

        // Adding to the catalog needs the boq Add permission (estimators add activities
        // they need while pricing); admins bypass via PermissionService.
        grp.MapPost("/", async (ActivityTypeInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            if (!await perm.CanAsync(me, "boq", ModuleAction.Add))
                return Results.Json(new { error = "Missing 'Add' permission on boq" }, statusCode: 403);
            var name = (i.Name ?? "").Trim();
            if (name.Length == 0) return Results.Json(new { error = "Name is required." }, statusCode: 400);
            if (await db.ActivityTypes.AnyAsync(a => a.Name.ToLower() == name.ToLower()))
                return Results.Json(new { error = $"An activity '{name}' already exists." }, statusCode: 409);

            var a = new ActivityType { Name = name, SortOrder = i.SortOrder, IsActive = true, Builtin = false };
            db.ActivityTypes.Add(a);
            await db.SaveChangesAsync();
            return Results.Created($"/api/activities/{a.Id}", ToDto(a));
        });

        grp.MapPut("/{id:int}", async (int id, ActivityTypeInput i, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var a = await db.ActivityTypes.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            var name = (i.Name ?? "").Trim();
            if (name.Length == 0) return Results.Json(new { error = "Name is required." }, statusCode: 400);
            if (await db.ActivityTypes.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower()))
                return Results.Json(new { error = $"An activity '{name}' already exists." }, statusCode: 409);

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
            var a = await db.ActivityTypes.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            if (a.Builtin) return Results.Json(new { error = "Built-in activities cannot be deleted (deactivate it instead)." }, statusCode: 409);
            db.ActivityTypes.Remove(a);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static IResult Forbid() => Results.Json(new { error = "Only a tenant admin can manage activities" }, statusCode: 403);
    private static ActivityTypeDto ToDto(ActivityType a) => new(a.Id, a.Name, a.SortOrder, a.IsActive, a.Builtin);
}
