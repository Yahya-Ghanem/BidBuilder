using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

public record AssemblyListDto(int Id, string Code, string Name, string Unit, decimal ComputedRate, bool IsActive, int ComponentCount);
public record ComponentDto(int Id, string ResourceType, int ResourceId, string ResourceCode, string ResourceName, decimal Factor, decimal ResourceRate, decimal Cost, string? Note, int SortOrder);
public record AssemblyDetailDto(int Id, string Code, string Name, string Unit, decimal ComputedRate, bool IsActive, List<ComponentDto> Components);
public record AssemblyInput(string Code, string Name, string? Unit, bool IsActive);
public record ComponentInput(string ResourceType, int ResourceId, decimal Factor, string? Note, int SortOrder);

/// <summary>
/// Assemblies (reusable unit-rate build-ups) + their components. Gated by the
/// <c>assemblies</c> module. Any change to components recomputes the unit rate.
/// </summary>
public static class AssemblyEndpoints
{
    private const string Mod = "assemblies";

    public static void MapAssemblyEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/assemblies").RequireAuthorization();

        // GET / — list with computed rate. Optional ?active=true|false filters by status (omit = all).
        grp.MapGet("/", async (ClaimsPrincipal me, PermissionService perm, AppDbContext db, bool? active) =>
            await Guard(me, perm, ModuleAction.View) ??
            Results.Ok(await db.Assemblies
                .Where(a => active == null || a.IsActive == active)
                .OrderBy(a => a.Code)
                .Select(a => new AssemblyListDto(a.Id, a.Code, a.Name, a.Unit, a.ComputedRate, a.IsActive, a.Components.Count))
                .ToListAsync()));

        // GET /{id} — detail with resolved component costs.
        grp.MapGet("/{id:int}", async (int id, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateEngine engine) =>
        {
            var g = await Guard(me, perm, ModuleAction.View); if (g is not null) return g;
            var a = await db.Assemblies.Include(x => x.Components).FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return NotFound();
            return Results.Ok(await ToDetail(a, engine));
        });

        // POST / — create header.
        grp.MapPost("/", async (AssemblyInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Add); if (g is not null) return g;
            var code = i.Code.Trim();
            if (await db.Assemblies.AnyAsync(a => a.Code == code)) return Dup(code);
            var a = new Assembly { Code = code, Name = i.Name.Trim(), Unit = i.Unit ?? "", IsActive = i.IsActive };
            db.Assemblies.Add(a); await db.SaveChangesAsync();
            return Results.Created($"/api/assemblies/{a.Id}",
                new AssemblyListDto(a.Id, a.Code, a.Name, a.Unit, a.ComputedRate, a.IsActive, 0));
        });

        // PUT /{id} — update header.
        grp.MapPut("/{id:int}", async (int id, AssemblyInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var a = await db.Assemblies.FirstOrDefaultAsync(x => x.Id == id); if (a is null) return NotFound();
            a.Name = i.Name.Trim(); a.Unit = i.Unit ?? ""; a.IsActive = i.IsActive; a.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new AssemblyListDto(a.Id, a.Code, a.Name, a.Unit, a.ComputedRate, a.IsActive, await db.AssemblyComponents.CountAsync(c => c.AssemblyId == id)));
        });

        // DELETE /{id}
        grp.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Delete); if (g is not null) return g;
            var a = await db.Assemblies.FirstOrDefaultAsync(x => x.Id == id); if (a is null) return NotFound();
            var uses = await db.BoqItems.CountAsync(i => i.AssemblyId == id);
            if (uses > 0)
                return Results.Json(new { error = $"Cannot delete — this assembly prices {uses} BOQ item(s). Repoint or remove those items first." }, statusCode: 409);
            db.Assemblies.Remove(a); await db.SaveChangesAsync(); return Results.NoContent();
        });

        // POST /{id}/components — add a build-up line, then recompute.
        grp.MapPost("/{id:int}/components", async (int id, ComponentInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateEngine engine) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var a = await db.Assemblies.FirstOrDefaultAsync(x => x.Id == id); if (a is null) return NotFound();
            if (!Enum.TryParse<ResourceType>(i.ResourceType, true, out var type))
                return Results.BadRequest(new { error = $"Invalid resourceType '{i.ResourceType}'" });
            if (!await engine.ResourceExistsAsync(type, i.ResourceId))
                return Results.BadRequest(new { error = $"{type} resource {i.ResourceId} not found" });

            db.AssemblyComponents.Add(new AssemblyComponent
            {
                AssemblyId = id, ResourceType = type, ResourceId = i.ResourceId,
                Factor = i.Factor, Note = i.Note, SortOrder = i.SortOrder,
            });
            await db.SaveChangesAsync();
            await engine.RecomputeAssemblyAsync(id);

            var fresh = await db.Assemblies.Include(x => x.Components).FirstAsync(x => x.Id == id);
            return Results.Ok(await ToDetail(fresh, engine));
        });

        // PUT /{id}/components/{cid} — update a line, then recompute.
        grp.MapPut("/{id:int}/components/{cid:int}", async (int id, int cid, ComponentInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateEngine engine) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var c = await db.AssemblyComponents.FirstOrDefaultAsync(x => x.Id == cid && x.AssemblyId == id);
            if (c is null) return NotFound();
            if (!Enum.TryParse<ResourceType>(i.ResourceType, true, out var type))
                return Results.BadRequest(new { error = $"Invalid resourceType '{i.ResourceType}'" });
            if (!await engine.ResourceExistsAsync(type, i.ResourceId))
                return Results.BadRequest(new { error = $"{type} resource {i.ResourceId} not found" });

            c.ResourceType = type; c.ResourceId = i.ResourceId; c.Factor = i.Factor; c.Note = i.Note; c.SortOrder = i.SortOrder;
            await db.SaveChangesAsync();
            await engine.RecomputeAssemblyAsync(id);

            var fresh = await db.Assemblies.Include(x => x.Components).FirstAsync(x => x.Id == id);
            return Results.Ok(await ToDetail(fresh, engine));
        });

        // DELETE /{id}/components/{cid} — remove a line, then recompute.
        grp.MapDelete("/{id:int}/components/{cid:int}", async (int id, int cid, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateEngine engine) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var c = await db.AssemblyComponents.FirstOrDefaultAsync(x => x.Id == cid && x.AssemblyId == id);
            if (c is null) return NotFound();
            db.AssemblyComponents.Remove(c); await db.SaveChangesAsync();
            await engine.RecomputeAssemblyAsync(id);

            var fresh = await db.Assemblies.Include(x => x.Components).FirstAsync(x => x.Id == id);
            return Results.Ok(await ToDetail(fresh, engine));
        });
    }

    private static async Task<AssemblyDetailDto> ToDetail(Assembly a, RateEngine engine)
    {
        var comps = new List<ComponentDto>();
        foreach (var c in a.Components.OrderBy(x => x.SortOrder))
        {
            var r = await engine.ResolveAsync(c.ResourceType, c.ResourceId, c.Factor);
            comps.Add(new ComponentDto(
                c.Id, c.ResourceType.ToString(), c.ResourceId,
                r?.ResourceCode ?? "(missing)", r?.ResourceName ?? "(missing resource)",
                c.Factor, r?.ResourceRate ?? 0m, r?.Cost ?? 0m, c.Note, c.SortOrder));
        }
        return new AssemblyDetailDto(a.Id, a.Code, a.Name, a.Unit, a.ComputedRate, a.IsActive, comps);
    }

    private static async Task<IResult?> Guard(ClaimsPrincipal me, PermissionService perm, ModuleAction action) =>
        await perm.CanAsync(me, Mod, action)
            ? null
            : Results.Json(new { error = $"Missing '{action}' permission on assemblies" }, statusCode: 403);

    private static IResult Dup(string code) => Results.Conflict(new { error = $"Code '{code}' already exists" });
    private static IResult NotFound() => Results.NotFound(new { error = "Not found" });
}
