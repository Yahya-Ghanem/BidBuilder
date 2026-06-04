using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────
public record LaborDto(int Id, string Code, string Name, string Unit, decimal RatePerHour, bool IsActive);
public record LaborInput(string Code, string Name, string? Unit, decimal RatePerHour, bool IsActive);

public record MaterialDto(int Id, string Code, string Name, string Unit, decimal UnitPrice, decimal WastagePct, string? Supplier, bool IsActive);
public record MaterialInput(string Code, string Name, string Unit, decimal UnitPrice, decimal WastagePct, string? Supplier, bool IsActive);

public record EquipmentDto(int Id, string Code, string Name, string Unit, decimal RatePerHour, bool IsActive);
public record EquipmentInput(string Code, string Name, string? Unit, decimal RatePerHour, bool IsActive);

public record SubcontractorDto(int Id, string Code, string Name, string Unit, decimal UnitRate, bool IsActive);
public record SubcontractorInput(string Code, string Name, string Unit, decimal UnitRate, bool IsActive);

/// <summary>
/// CRUD for the tenant-shared resource library. All routes require the
/// <c>resource-library</c> module permission for the relevant action.
/// </summary>
public static class ResourceEndpoints
{
    private const string Mod = "resource-library";

    public static void MapResourceEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/resources").RequireAuthorization();

        // ── Labor ──────────────────────────────────────────────────────────────
        var labor = grp.MapGroup("/labor");
        labor.MapGet("/", async (ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
            await Guard(me, perm, ModuleAction.View) ??
            Results.Ok(await db.LaborResources.OrderBy(r => r.Code)
                .Select(r => new LaborDto(r.Id, r.Code, r.Name, r.Unit, r.RatePerHour, r.IsActive)).ToListAsync()));

        labor.MapPost("/", async (LaborInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Add); if (g is not null) return g;
            if (await db.LaborResources.AnyAsync(r => r.Code == i.Code)) return Dup(i.Code);
            var e = new LaborResource { Code = i.Code.Trim(), Name = i.Name.Trim(), Unit = i.Unit ?? "hr", RatePerHour = i.RatePerHour, IsActive = i.IsActive };
            db.LaborResources.Add(e); await db.SaveChangesAsync();
            return Results.Created($"/api/resources/labor/{e.Id}", new LaborDto(e.Id, e.Code, e.Name, e.Unit, e.RatePerHour, e.IsActive));
        });

        labor.MapPut("/{id:int}", async (int id, LaborInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.LaborResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            e.Name = i.Name.Trim(); e.Unit = i.Unit ?? "hr"; e.RatePerHour = i.RatePerHour; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await cascade.OnResourceChangedAsync(ResourceType.Labor, e.Id);
            return Results.Ok(new LaborDto(e.Id, e.Code, e.Name, e.Unit, e.RatePerHour, e.IsActive));
        });

        labor.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Delete); if (g is not null) return g;
            var e = await db.LaborResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var inUse = await InUse(db, ResourceType.Labor, id); if (inUse is not null) return inUse;
            db.LaborResources.Remove(e); await db.SaveChangesAsync(); return Results.NoContent();
        });

        // ── Materials ────────────────────────────────────────────────────────────
        var mat = grp.MapGroup("/materials");
        mat.MapGet("/", async (ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
            await Guard(me, perm, ModuleAction.View) ??
            Results.Ok(await db.MaterialResources.OrderBy(r => r.Code)
                .Select(r => new MaterialDto(r.Id, r.Code, r.Name, r.Unit, r.UnitPrice, r.WastagePct, r.Supplier, r.IsActive)).ToListAsync()));

        mat.MapPost("/", async (MaterialInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Add); if (g is not null) return g;
            if (await db.MaterialResources.AnyAsync(r => r.Code == i.Code)) return Dup(i.Code);
            var e = new MaterialResource { Code = i.Code.Trim(), Name = i.Name.Trim(), Unit = i.Unit, UnitPrice = i.UnitPrice, WastagePct = i.WastagePct, Supplier = i.Supplier, IsActive = i.IsActive };
            db.MaterialResources.Add(e); await db.SaveChangesAsync();
            return Results.Created($"/api/resources/materials/{e.Id}", new MaterialDto(e.Id, e.Code, e.Name, e.Unit, e.UnitPrice, e.WastagePct, e.Supplier, e.IsActive));
        });

        mat.MapPut("/{id:int}", async (int id, MaterialInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.MaterialResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            e.Name = i.Name.Trim(); e.Unit = i.Unit; e.UnitPrice = i.UnitPrice; e.WastagePct = i.WastagePct; e.Supplier = i.Supplier; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await cascade.OnResourceChangedAsync(ResourceType.Material, e.Id);
            return Results.Ok(new MaterialDto(e.Id, e.Code, e.Name, e.Unit, e.UnitPrice, e.WastagePct, e.Supplier, e.IsActive));
        });

        mat.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Delete); if (g is not null) return g;
            var e = await db.MaterialResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var inUse = await InUse(db, ResourceType.Material, id); if (inUse is not null) return inUse;
            db.MaterialResources.Remove(e); await db.SaveChangesAsync(); return Results.NoContent();
        });

        // ── Equipment ────────────────────────────────────────────────────────────
        var eq = grp.MapGroup("/equipment");
        eq.MapGet("/", async (ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
            await Guard(me, perm, ModuleAction.View) ??
            Results.Ok(await db.EquipmentResources.OrderBy(r => r.Code)
                .Select(r => new EquipmentDto(r.Id, r.Code, r.Name, r.Unit, r.RatePerHour, r.IsActive)).ToListAsync()));

        eq.MapPost("/", async (EquipmentInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Add); if (g is not null) return g;
            if (await db.EquipmentResources.AnyAsync(r => r.Code == i.Code)) return Dup(i.Code);
            var e = new EquipmentResource { Code = i.Code.Trim(), Name = i.Name.Trim(), Unit = i.Unit ?? "hr", RatePerHour = i.RatePerHour, IsActive = i.IsActive };
            db.EquipmentResources.Add(e); await db.SaveChangesAsync();
            return Results.Created($"/api/resources/equipment/{e.Id}", new EquipmentDto(e.Id, e.Code, e.Name, e.Unit, e.RatePerHour, e.IsActive));
        });

        eq.MapPut("/{id:int}", async (int id, EquipmentInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.EquipmentResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            e.Name = i.Name.Trim(); e.Unit = i.Unit ?? "hr"; e.RatePerHour = i.RatePerHour; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await cascade.OnResourceChangedAsync(ResourceType.Equipment, e.Id);
            return Results.Ok(new EquipmentDto(e.Id, e.Code, e.Name, e.Unit, e.RatePerHour, e.IsActive));
        });

        eq.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Delete); if (g is not null) return g;
            var e = await db.EquipmentResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var inUse = await InUse(db, ResourceType.Equipment, id); if (inUse is not null) return inUse;
            db.EquipmentResources.Remove(e); await db.SaveChangesAsync(); return Results.NoContent();
        });

        // ── Subcontractors ───────────────────────────────────────────────────────
        var sub = grp.MapGroup("/subcontractors");
        sub.MapGet("/", async (ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
            await Guard(me, perm, ModuleAction.View) ??
            Results.Ok(await db.Subcontractors.OrderBy(r => r.Code)
                .Select(r => new SubcontractorDto(r.Id, r.Code, r.Name, r.Unit, r.UnitRate, r.IsActive)).ToListAsync()));

        sub.MapPost("/", async (SubcontractorInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Add); if (g is not null) return g;
            if (await db.Subcontractors.AnyAsync(r => r.Code == i.Code)) return Dup(i.Code);
            var e = new Subcontractor { Code = i.Code.Trim(), Name = i.Name.Trim(), Unit = i.Unit, UnitRate = i.UnitRate, IsActive = i.IsActive };
            db.Subcontractors.Add(e); await db.SaveChangesAsync();
            return Results.Created($"/api/resources/subcontractors/{e.Id}", new SubcontractorDto(e.Id, e.Code, e.Name, e.Unit, e.UnitRate, e.IsActive));
        });

        sub.MapPut("/{id:int}", async (int id, SubcontractorInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.Subcontractors.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            e.Name = i.Name.Trim(); e.Unit = i.Unit; e.UnitRate = i.UnitRate; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await cascade.OnResourceChangedAsync(ResourceType.Subcontractor, e.Id);
            return Results.Ok(new SubcontractorDto(e.Id, e.Code, e.Name, e.Unit, e.UnitRate, e.IsActive));
        });

        sub.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Delete); if (g is not null) return g;
            var e = await db.Subcontractors.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var inUse = await InUse(db, ResourceType.Subcontractor, id); if (inUse is not null) return inUse;
            db.Subcontractors.Remove(e); await db.SaveChangesAsync(); return Results.NoContent();
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static async Task<IResult?> Guard(ClaimsPrincipal me, PermissionService perm, ModuleAction action) =>
        await perm.CanAsync(me, Mod, action)
            ? null
            : Results.Json(new { error = $"Missing '{action}' permission on resource-library" }, statusCode: 403);

    /// <summary>409 (listing the dependent assemblies) if any assembly component
    /// references this resource — so a delete can't silently orphan a build-up.</summary>
    private static async Task<IResult?> InUse(AppDbContext db, ResourceType type, int resourceId)
    {
        var codes = await db.AssemblyComponents
            .Where(c => c.ResourceType == type && c.ResourceId == resourceId)
            .Select(c => c.Assembly.Code)
            .Distinct()
            .ToListAsync();
        return codes.Count == 0
            ? null
            : Results.Json(new { error = $"Cannot delete — this resource is used by {codes.Count} assembly(ies): {string.Join(", ", codes)}. Remove it from those assemblies first." }, statusCode: 409);
    }

    private static IResult Dup(string code) => Results.Conflict(new { error = $"Code '{code}' already exists" });
    private static IResult NotFound() => Results.NotFound(new { error = "Resource not found" });
}
