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

// ── Dated rate history (18.3) ────────────────────────────────────────────────
public record RateHistoryDto(int Id, string ResourceType, int ResourceId, DateOnly EffectiveFrom, decimal Rate, string? Source, DateTime CreatedAt);
/// <summary>Manual back-date entry: estimator records "this rate was effective from X".</summary>
public record RateHistoryInput(DateOnly EffectiveFrom, decimal Rate, string? Source);

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
            var code = i.Code.Trim();
            if (await db.LaborResources.AnyAsync(r => r.Code == code)) return Dup(code);
            var e = new LaborResource { Code = code, Name = i.Name.Trim(), Unit = i.Unit ?? "hr", RatePerHour = i.RatePerHour, IsActive = i.IsActive };
            db.LaborResources.Add(e); await db.SaveChangesAsync();
            return Results.Created($"/api/resources/labor/{e.Id}", new LaborDto(e.Id, e.Code, e.Name, e.Unit, e.RatePerHour, e.IsActive));
        });

        labor.MapPut("/{id:int}", async (int id, LaborInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.LaborResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var oldRate = e.RatePerHour;
            e.Name = i.Name.Trim(); e.Unit = i.Unit ?? "hr"; e.RatePerHour = i.RatePerHour; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            RecordHistoryIfRateChanged(db, ResourceType.Labor, e.Id, oldRate, e.RatePerHour, "rate-change");
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
            var code = i.Code.Trim();
            if (await db.MaterialResources.AnyAsync(r => r.Code == code)) return Dup(code);
            var e = new MaterialResource { Code = code, Name = i.Name.Trim(), Unit = i.Unit, UnitPrice = i.UnitPrice, WastagePct = i.WastagePct, Supplier = i.Supplier, IsActive = i.IsActive };
            db.MaterialResources.Add(e); await db.SaveChangesAsync();
            return Results.Created($"/api/resources/materials/{e.Id}", new MaterialDto(e.Id, e.Code, e.Name, e.Unit, e.UnitPrice, e.WastagePct, e.Supplier, e.IsActive));
        });

        mat.MapPut("/{id:int}", async (int id, MaterialInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.MaterialResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var oldRate = e.UnitPrice;
            e.Name = i.Name.Trim(); e.Unit = i.Unit; e.UnitPrice = i.UnitPrice; e.WastagePct = i.WastagePct; e.Supplier = i.Supplier; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            RecordHistoryIfRateChanged(db, ResourceType.Material, e.Id, oldRate, e.UnitPrice, "rate-change");
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
            var code = i.Code.Trim();
            if (await db.EquipmentResources.AnyAsync(r => r.Code == code)) return Dup(code);
            var e = new EquipmentResource { Code = code, Name = i.Name.Trim(), Unit = i.Unit ?? "hr", RatePerHour = i.RatePerHour, IsActive = i.IsActive };
            db.EquipmentResources.Add(e); await db.SaveChangesAsync();
            return Results.Created($"/api/resources/equipment/{e.Id}", new EquipmentDto(e.Id, e.Code, e.Name, e.Unit, e.RatePerHour, e.IsActive));
        });

        eq.MapPut("/{id:int}", async (int id, EquipmentInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.EquipmentResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var oldRate = e.RatePerHour;
            e.Name = i.Name.Trim(); e.Unit = i.Unit ?? "hr"; e.RatePerHour = i.RatePerHour; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            RecordHistoryIfRateChanged(db, ResourceType.Equipment, e.Id, oldRate, e.RatePerHour, "rate-change");
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
            var code = i.Code.Trim();
            if (await db.Subcontractors.AnyAsync(r => r.Code == code)) return Dup(code);
            var e = new Subcontractor { Code = code, Name = i.Name.Trim(), Unit = i.Unit, UnitRate = i.UnitRate, IsActive = i.IsActive };
            db.Subcontractors.Add(e); await db.SaveChangesAsync();
            return Results.Created($"/api/resources/subcontractors/{e.Id}", new SubcontractorDto(e.Id, e.Code, e.Name, e.Unit, e.UnitRate, e.IsActive));
        });

        sub.MapPut("/{id:int}", async (int id, SubcontractorInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.Subcontractors.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var oldRate = e.UnitRate;
            e.Name = i.Name.Trim(); e.Unit = i.Unit; e.UnitRate = i.UnitRate; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            RecordHistoryIfRateChanged(db, ResourceType.Subcontractor, e.Id, oldRate, e.UnitRate, "rate-change");
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

        // ── Dated rate history (18.3) ────────────────────────────────────────
        // GET → all rate snapshots for the resource, newest-first
        // POST → manually insert a back-dated snapshot (estimator records "this
        //   was the rate from date X" — supplier PO, quote ref, etc.)
        // DELETE → remove a stray snapshot
        grp.MapGet("/{type}/{id:int}/history", async (string type, int id, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.View); if (g is not null) return g;
            if (!TryParseType(type, out var rt)) return BadResourceType(type);
            var rows = await db.ResourceRateHistory
                .Where(h => h.ResourceType == rt && h.ResourceId == id)
                .OrderByDescending(h => h.EffectiveFrom).ThenByDescending(h => h.Id)
                .Select(h => new RateHistoryDto(h.Id, h.ResourceType.ToString(), h.ResourceId, h.EffectiveFrom, h.Rate, h.Source, h.CreatedAt))
                .ToListAsync();
            return Results.Ok(rows);
        });

        grp.MapPost("/{type}/{id:int}/history", async (string type, int id, RateHistoryInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            if (!TryParseType(type, out var rt)) return BadResourceType(type);
            if (i.Rate < 0) return Results.Json(new { error = "Rate cannot be negative." }, statusCode: 400);
            var h = new ResourceRateHistory { ResourceType = rt, ResourceId = id, EffectiveFrom = i.EffectiveFrom, Rate = i.Rate, Source = i.Source?.Trim() };
            db.ResourceRateHistory.Add(h);
            await db.SaveChangesAsync();
            // A back-date snapshot does NOT touch the live resource rate, but it can
            // alter any estimate with a PricingDate on/after EffectiveFrom; cascade so
            // those estimates re-roll their bid (the live cascade traverses by
            // resource→assembly→estimate identically here).
            await cascade.OnResourceChangedAsync(rt, id);
            return Results.Created($"/api/resources/{type}/{id}/history/{h.Id}",
                new RateHistoryDto(h.Id, h.ResourceType.ToString(), h.ResourceId, h.EffectiveFrom, h.Rate, h.Source, h.CreatedAt));
        });

        grp.MapDelete("/{type}/{id:int}/history/{historyId:int}", async (string type, int id, int historyId, ClaimsPrincipal me, PermissionService perm, AppDbContext db, RateCascadeService cascade) =>
        {
            var g = await Guard(me, perm, ModuleAction.Delete); if (g is not null) return g;
            if (!TryParseType(type, out var rt)) return BadResourceType(type);
            var h = await db.ResourceRateHistory.FirstOrDefaultAsync(x => x.Id == historyId && x.ResourceType == rt && x.ResourceId == id);
            if (h is null) return Results.NotFound(new { error = "History entry not found" });
            db.ResourceRateHistory.Remove(h); await db.SaveChangesAsync();
            await cascade.OnResourceChangedAsync(rt, id);
            return Results.NoContent();
        });
    }

    /// <summary>Snapshot the prior rate into history when a resource's rate
    /// changes via the API. The effective-from date is "today" (UTC) — the snapshot
    /// captures the rate as it WAS up to this point. Manual back-dating uses the
    /// dedicated POST /history endpoint.</summary>
    private static void RecordHistoryIfRateChanged(AppDbContext db, ResourceType type, int resourceId, decimal oldRate, decimal newRate, string source)
    {
        if (oldRate == newRate) return;
        db.ResourceRateHistory.Add(new ResourceRateHistory
        {
            ResourceType = type,
            ResourceId = resourceId,
            EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow),
            Rate = oldRate,    // snapshot the rate that just ended
            Source = source,
        });
    }

    /// <summary>Accept either the route segment (e.g. "materials") or the canonical
    /// enum name. False if unknown.</summary>
    public static bool TryParseType(string raw, out ResourceType type)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "labor":          type = ResourceType.Labor;         return true;
            case "materials":
            case "material":       type = ResourceType.Material;      return true;
            case "equipment":      type = ResourceType.Equipment;     return true;
            case "subcontractors":
            case "subcontractor":  type = ResourceType.Subcontractor; return true;
            default:               type = ResourceType.Labor;         return false;
        }
    }

    private static IResult BadResourceType(string raw) =>
        Results.Json(new { error = $"Unknown resource type '{raw}'. Use labor|materials|equipment|subcontractors." }, statusCode: 400);

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
