using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

public record AreaDto(int Id, int? ParentAreaId, string Name, string? Code, string Kind, int SortOrder);
public record AreaInput(string Name, string? Code, string Kind, int? ParentAreaId, int SortOrder);

/// <summary>
/// Project area breakdown (Area → Sub-area → Unit, nesting via ParentAreaId) plus
/// the per-estimate cost roll-up that escalates item line totals up the tree.
/// Areas are project-scoped and shared across estimate revisions; gated by project
/// access + the <c>projects</c> module permission.
/// </summary>
public static class AreaEndpoints
{
    private const string Mod = "projects";

    public static void MapAreaEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/projects/{pid:int}/areas").RequireAuthorization();

        grp.MapGet("/", async (int pid, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db) =>
        {
            if (!await access.Accessible(me).AnyAsync(p => p.Id == pid)) return NotFound();
            if (!await perm.CanAsync(me, Mod, ModuleAction.View)) return Forbid(Mod, "View");
            var areas = await db.Areas.Where(a => a.ProjectId == pid)
                .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
                .Select(a => new AreaDto(a.Id, a.ParentAreaId, a.Name, a.Code, a.Kind.ToString(), a.SortOrder))
                .ToListAsync();
            return Results.Ok(areas);
        });

        grp.MapPost("/", async (int pid, AreaInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db) =>
        {
            if (!await access.Accessible(me).AnyAsync(p => p.Id == pid)) return NotFound();
            if (!await perm.CanAsync(me, Mod, ModuleAction.Add)) return Forbid(Mod, "Add");
            var (kind, err) = ParseKind(i.Kind); if (err is not null) return err;
            if (string.IsNullOrWhiteSpace(i.Name)) return Bad("Name is required.");
            if (i.ParentAreaId is int p && !await db.Areas.AnyAsync(a => a.Id == p && a.ProjectId == pid))
                return Bad("Parent area not found in this project.");

            var area = new Area { ProjectId = pid, ParentAreaId = i.ParentAreaId, Name = i.Name.Trim(), Code = Trim(i.Code), Kind = kind, SortOrder = i.SortOrder };
            db.Areas.Add(area);
            await db.SaveChangesAsync();
            return Results.Created($"/api/projects/{pid}/areas/{area.Id}",
                new AreaDto(area.Id, area.ParentAreaId, area.Name, area.Code, area.Kind.ToString(), area.SortOrder));
        });

        grp.MapPut("/{aid:int}", async (int pid, int aid, AreaInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db) =>
        {
            if (!await access.Accessible(me).AnyAsync(p => p.Id == pid)) return NotFound();
            if (!await perm.CanAsync(me, Mod, ModuleAction.Edit)) return Forbid(Mod, "Edit");
            var area = await db.Areas.FirstOrDefaultAsync(a => a.Id == aid && a.ProjectId == pid); if (area is null) return NotFound();
            var (kind, err) = ParseKind(i.Kind); if (err is not null) return err;
            if (string.IsNullOrWhiteSpace(i.Name)) return Bad("Name is required.");
            // Re-parenting: must stay within the project and not point at itself.
            if (i.ParentAreaId is int p)
            {
                if (p == aid) return Bad("An area cannot be its own parent.");
                if (!await db.Areas.AnyAsync(a => a.Id == p && a.ProjectId == pid)) return Bad("Parent area not found in this project.");
            }
            area.Name = i.Name.Trim(); area.Code = Trim(i.Code); area.Kind = kind; area.ParentAreaId = i.ParentAreaId; area.SortOrder = i.SortOrder;
            await db.SaveChangesAsync();
            return Results.Ok(new AreaDto(area.Id, area.ParentAreaId, area.Name, area.Code, area.Kind.ToString(), area.SortOrder));
        });

        grp.MapDelete("/{aid:int}", async (int pid, int aid, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db) =>
        {
            if (!await access.Accessible(me).AnyAsync(p => p.Id == pid)) return NotFound();
            if (!await perm.CanAsync(me, Mod, ModuleAction.Delete)) return Forbid(Mod, "Delete");
            var area = await db.Areas.FirstOrDefaultAsync(a => a.Id == aid && a.ProjectId == pid); if (area is null) return NotFound();
            if (await db.Areas.AnyAsync(a => a.ParentAreaId == aid))
                return Results.Json(new { error = "Cannot delete — this area has sub-areas. Delete or move them first." }, statusCode: 409);
            var uses = await db.BoqItems.CountAsync(it => it.AreaId == aid);
            if (uses > 0) return Results.Json(new { error = $"Cannot delete — {uses} BOQ item(s) are assigned to this area. Reassign them first." }, statusCode: 409);
            db.Areas.Remove(area);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ── Per-estimate cost roll-up (unit → sub-area → area → project) ───────
        app.MapGet("/api/estimates/{id:int}/areas-rollup", async (int id, ClaimsPrincipal me, ProjectAccessService access, AreaRollupService rollup) =>
        {
            if (!await access.CanAccessEstimateAsync(me, id)) return NotFound();
            var result = await rollup.ComputeAsync(id);
            return result is null ? NotFound() : Results.Ok(result);
        }).RequireAuthorization();
    }

    private static (AreaKind kind, IResult? err) ParseKind(string? raw) =>
        Enum.TryParse<AreaKind>((raw ?? "Area").Trim(), true, out var k) ? (k, null)
        : (AreaKind.Area, Bad("Kind must be Area, SubArea or Unit."));

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static IResult Bad(string msg) => Results.BadRequest(new { error = msg });
    private static IResult NotFound() => Results.NotFound(new { error = "Not found or not accessible" });
    private static IResult Forbid(string mod, string act) => Results.Json(new { error = $"Missing '{act}' permission on {mod}" }, statusCode: 403);
}
