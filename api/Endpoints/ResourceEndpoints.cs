using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;
using BidBuilder.Api.Validation;

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

// ── 23.4 — Rate trend (sparkline + volatility) ───────────────────────────────
/// <summary>One bucket on the trend chart: a year-month label ("2025-04") and the
/// arithmetic-mean rate inside that month. Months with no data are omitted (the chart
/// renders linear segments between adjacent points).</summary>
public record RateTrendPoint(string Month, decimal Rate);
/// <summary>24-month rate trend for a single library resource (Labor / Material /
/// Equipment / Subcontractor). Summary fields are computed over the window so the
/// UI can render a sparkline + an "low/med/high" volatility badge without re-walking
/// the series client-side. <see cref="VolatilityIndex"/> = stddev / mean of the
/// monthly series (Coefficient of Variation); null when the series is too thin.</summary>
public record RateTrendDto(
    string ResourceType, int ResourceId, decimal CurrentRate,
    decimal? Min12m, decimal? Max12m, decimal? VolatilityIndex,
    IReadOnlyList<RateTrendPoint> Points);

// ── Bulk operations (20.12) ──────────────────────────────────────────────────
/// <summary>Apply one action to many resources of a type at once.
/// <c>Action</c> ∈ {activate, deactivate, delete}.</summary>
public record BulkResourceRequest(int[] Ids, string Action);
/// <summary>One resource a bulk action could not apply to, with why.</summary>
public record BulkSkip(int Id, string Reason);
/// <summary>Outcome of a bulk action: how many were updated/deleted and which were skipped.</summary>
public record BulkResourceResult(string Action, int Updated, int Deleted, IReadOnlyList<BulkSkip> Skipped);

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
        }).AddEndpointFilter<ValidationFilter<LaborInput>>();

        labor.MapPut("/{id:int}", async (int id, LaborInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, ICascadeQueue queue, ITenantContext tenant) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.LaborResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var oldRate = e.RatePerHour;
            e.Name = i.Name.Trim(); e.Unit = i.Unit ?? "hr"; e.RatePerHour = i.RatePerHour; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            RecordHistoryIfRateChanged(db, ResourceType.Labor, e.Id, oldRate, e.RatePerHour, "rate-change");
            await db.SaveChangesAsync();
            // Fan-out is fire-and-forget (Hangfire job in prod; inline in tests).
            await queue.EnqueueResourceChangedAsync(tenant.TenantId, ResourceType.Labor, e.Id);
            return Results.Ok(new LaborDto(e.Id, e.Code, e.Name, e.Unit, e.RatePerHour, e.IsActive));
        }).AddEndpointFilter<ValidationFilter<LaborInput>>();

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
        }).AddEndpointFilter<ValidationFilter<MaterialInput>>();

        mat.MapPut("/{id:int}", async (int id, MaterialInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, ICascadeQueue queue, ITenantContext tenant) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.MaterialResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var oldRate = e.UnitPrice;
            e.Name = i.Name.Trim(); e.Unit = i.Unit; e.UnitPrice = i.UnitPrice; e.WastagePct = i.WastagePct; e.Supplier = i.Supplier; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            RecordHistoryIfRateChanged(db, ResourceType.Material, e.Id, oldRate, e.UnitPrice, "rate-change");
            await db.SaveChangesAsync();
            await queue.EnqueueResourceChangedAsync(tenant.TenantId, ResourceType.Material, e.Id);
            return Results.Ok(new MaterialDto(e.Id, e.Code, e.Name, e.Unit, e.UnitPrice, e.WastagePct, e.Supplier, e.IsActive));
        }).AddEndpointFilter<ValidationFilter<MaterialInput>>();

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
        }).AddEndpointFilter<ValidationFilter<EquipmentInput>>();

        eq.MapPut("/{id:int}", async (int id, EquipmentInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, ICascadeQueue queue, ITenantContext tenant) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.EquipmentResources.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var oldRate = e.RatePerHour;
            e.Name = i.Name.Trim(); e.Unit = i.Unit ?? "hr"; e.RatePerHour = i.RatePerHour; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            RecordHistoryIfRateChanged(db, ResourceType.Equipment, e.Id, oldRate, e.RatePerHour, "rate-change");
            await db.SaveChangesAsync();
            await queue.EnqueueResourceChangedAsync(tenant.TenantId, ResourceType.Equipment, e.Id);
            return Results.Ok(new EquipmentDto(e.Id, e.Code, e.Name, e.Unit, e.RatePerHour, e.IsActive));
        }).AddEndpointFilter<ValidationFilter<EquipmentInput>>();

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
        }).AddEndpointFilter<ValidationFilter<SubcontractorInput>>();

        sub.MapPut("/{id:int}", async (int id, SubcontractorInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, ICascadeQueue queue, ITenantContext tenant) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var e = await db.Subcontractors.FirstOrDefaultAsync(r => r.Id == id); if (e is null) return NotFound();
            var oldRate = e.UnitRate;
            e.Name = i.Name.Trim(); e.Unit = i.Unit; e.UnitRate = i.UnitRate; e.IsActive = i.IsActive; e.UpdatedAt = DateTime.UtcNow;
            RecordHistoryIfRateChanged(db, ResourceType.Subcontractor, e.Id, oldRate, e.UnitRate, "rate-change");
            await db.SaveChangesAsync();
            await queue.EnqueueResourceChangedAsync(tenant.TenantId, ResourceType.Subcontractor, e.Id);
            return Results.Ok(new SubcontractorDto(e.Id, e.Code, e.Name, e.Unit, e.UnitRate, e.IsActive));
        }).AddEndpointFilter<ValidationFilter<SubcontractorInput>>();

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

        grp.MapPost("/{type}/{id:int}/history", async (string type, int id, RateHistoryInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db, ICascadeQueue queue, ITenantContext tenant) =>
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
            await queue.EnqueueResourceChangedAsync(tenant.TenantId, rt, id);
            return Results.Created($"/api/resources/{type}/{id}/history/{h.Id}",
                new RateHistoryDto(h.Id, h.ResourceType.ToString(), h.ResourceId, h.EffectiveFrom, h.Rate, h.Source, h.CreatedAt));
        }).AddEndpointFilter<ValidationFilter<RateHistoryInput>>();

        grp.MapDelete("/{type}/{id:int}/history/{historyId:int}", async (string type, int id, int historyId, ClaimsPrincipal me, PermissionService perm, AppDbContext db, ICascadeQueue queue, ITenantContext tenant) =>
        {
            var g = await Guard(me, perm, ModuleAction.Delete); if (g is not null) return g;
            if (!TryParseType(type, out var rt)) return BadResourceType(type);
            var h = await db.ResourceRateHistory.FirstOrDefaultAsync(x => x.Id == historyId && x.ResourceType == rt && x.ResourceId == id);
            if (h is null) return Results.NotFound(new { error = "History entry not found" });
            db.ResourceRateHistory.Remove(h); await db.SaveChangesAsync();
            await queue.EnqueueResourceChangedAsync(tenant.TenantId, rt, id);
            return Results.NoContent();
        });

        // ── 23.4 — Rate trend (sparkline + volatility) ─────────────────────────
        // GET → 24-month monthly-averaged rate series + summary stats (current /
        //   12-month min/max / volatility index). Aggregation runs in-memory: 18.3
        //   already caps per-resource history at the human-scale "a few hundred rows
        //   in 24 months", so the cost is negligible. The endpoint is read-only and
        //   never enqueues a cascade.
        grp.MapGet("/{type}/{id:int}/rate-trend", async (string type, int id, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.View); if (g is not null) return g;
            if (!TryParseType(type, out var rt)) return BadResourceType(type);

            var liveRate = await GetLiveRateAsync(db, rt, id);
            if (liveRate is null) return Results.NotFound(new { error = "Resource not found" });

            var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var windowStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-23);   // include 24 months
            var twelveMonthsAgo = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);

            // Load every dated point inside the 24-month window (plus the most recent point
            // BEFORE the window, so a long-dormant rate is shown as carried-forward).
            var inside = await db.ResourceRateHistory
                .Where(h => h.ResourceType == rt && h.ResourceId == id && h.EffectiveFrom >= windowStart)
                .OrderBy(h => h.EffectiveFrom)
                .Select(h => new { h.EffectiveFrom, h.Rate })
                .ToListAsync();
            var carryIn = await db.ResourceRateHistory
                .Where(h => h.ResourceType == rt && h.ResourceId == id && h.EffectiveFrom < windowStart)
                .OrderByDescending(h => h.EffectiveFrom)
                .Select(h => new { h.EffectiveFrom, h.Rate })
                .FirstOrDefaultAsync();

            // Bucket by year-month. Within a month average all rate points so a flurry of
            // updates doesn't skew the trend. If a month has no point, carry the last seen
            // rate forward so the sparkline is continuous (mirrors how the estimator's
            // pricing engine resolves a rate as-at a date).
            var byMonth = inside
                .GroupBy(h => new { h.EffectiveFrom.Year, h.EffectiveFrom.Month })
                .ToDictionary(g => g.Key, g => g.Average(x => x.Rate));

            var points = new List<RateTrendPoint>(24);
            decimal? lastSeen = carryIn?.Rate;
            for (var m = 0; m < 24; m++)
            {
                var d = windowStart.AddMonths(m);
                var key = new { d.Year, d.Month };
                if (byMonth.TryGetValue(key, out var rate))
                    lastSeen = rate;
                if (lastSeen is decimal r) points.Add(new RateTrendPoint($"{d.Year:D4}-{d.Month:D2}", decimal.Round(r, 4)));
            }
            // Ensure the FINAL point reflects the live rate (last known truth), in case the
            // most-recent history row is older than this month.
            if (points.Count > 0 && points[^1].Rate != liveRate.Value)
                points[^1] = new RateTrendPoint(points[^1].Month, decimal.Round(liveRate.Value, 4));

            // Summary stats. min/max over the last 12 months; volatility over the full window.
            decimal? min12m = null, max12m = null;
            foreach (var p in points)
            {
                var (yyyy, mm) = (int.Parse(p.Month[..4]), int.Parse(p.Month[5..]));
                if (new DateOnly(yyyy, mm, 1) >= twelveMonthsAgo)
                {
                    if (min12m is null || p.Rate < min12m) min12m = p.Rate;
                    if (max12m is null || p.Rate > max12m) max12m = p.Rate;
                }
            }
            decimal? vol = null;
            if (points.Count >= 2)
            {
                var values = points.Select(p => p.Rate).ToArray();
                var mean = values.Average();
                if (mean > 0)
                {
                    var variance = values.Select(v => (v - mean) * (v - mean)).Sum() / values.Length;
                    var stddev = (decimal)Math.Sqrt((double)variance);
                    vol = decimal.Round(stddev / mean, 4);
                }
            }

            return Results.Ok(new RateTrendDto(rt.ToString(), id, decimal.Round(liveRate.Value, 4),
                min12m, max12m, vol, points));
        });

        // ── Bulk operations (20.12) ──────────────────────────────────────────
        // One action across many resources of a type. activate/deactivate flip
        // IsActive (Edit perm); delete removes them (Delete perm) but SKIPS any
        // resource referenced by an assembly — those are reported, not orphaned.
        // IsActive never affects a computed rate, so no cascade is needed; deleted
        // resources have no dependents by definition, so none is needed there either.
        grp.MapPost("/{type}/bulk", async (string type, BulkResourceRequest req, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            if (!TryParseType(type, out var rt)) return BadResourceType(type);
            var action = (req.Action ?? "").Trim().ToLowerInvariant();
            var ids = (req.Ids ?? Array.Empty<int>()).Where(x => x > 0).Distinct().ToArray();
            if (ids.Length == 0) return Results.Json(new { error = "No resource ids supplied." }, statusCode: 400);

            switch (action)
            {
                case "activate":
                case "deactivate":
                {
                    var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
                    var active = action == "activate";
                    var now = DateTime.UtcNow;
                    int updated = rt switch
                    {
                        ResourceType.Labor         => await db.LaborResources.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, active).SetProperty(r => r.UpdatedAt, now)),
                        ResourceType.Material      => await db.MaterialResources.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, active).SetProperty(r => r.UpdatedAt, now)),
                        ResourceType.Equipment     => await db.EquipmentResources.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, active).SetProperty(r => r.UpdatedAt, now)),
                        ResourceType.Subcontractor => await db.Subcontractors.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, active).SetProperty(r => r.UpdatedAt, now)),
                        _ => 0,
                    };
                    return Results.Ok(new BulkResourceResult(action, updated, 0, Array.Empty<BulkSkip>()));
                }
                case "delete":
                {
                    var g = await Guard(me, perm, ModuleAction.Delete); if (g is not null) return g;
                    // Resources referenced by an assembly component cannot be deleted.
                    var blocked = await db.AssemblyComponents
                        .Where(c => c.ResourceType == rt && ids.Contains(c.ResourceId))
                        .Select(c => c.ResourceId).Distinct().ToListAsync();
                    var deletable = ids.Except(blocked).ToArray();
                    int deleted = deletable.Length == 0 ? 0 : rt switch
                    {
                        ResourceType.Labor         => await db.LaborResources.Where(r => deletable.Contains(r.Id)).ExecuteDeleteAsync(),
                        ResourceType.Material      => await db.MaterialResources.Where(r => deletable.Contains(r.Id)).ExecuteDeleteAsync(),
                        ResourceType.Equipment     => await db.EquipmentResources.Where(r => deletable.Contains(r.Id)).ExecuteDeleteAsync(),
                        ResourceType.Subcontractor => await db.Subcontractors.Where(r => deletable.Contains(r.Id)).ExecuteDeleteAsync(),
                        _ => 0,
                    };
                    var skipped = blocked.Select(id => new BulkSkip(id, "in use by an assembly")).ToList();
                    return Results.Ok(new BulkResourceResult(action, 0, deleted, skipped));
                }
                default:
                    return Results.Json(new { error = "Unknown action. Use activate|deactivate|delete." }, statusCode: 400);
            }
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

    /// <summary>23.4 — Look up the live rate for a resource by type+id, returning null if
    /// it doesn't exist. The "live rate" is the one currently on the resource row (per-hour
    /// for Labor/Equipment, unit price for Material, unit rate for Subcontractor) — what an
    /// estimate built TODAY would use. Used by the rate-trend endpoint to plant the final
    /// data point and the "current" summary.</summary>
    private static async Task<decimal?> GetLiveRateAsync(AppDbContext db, ResourceType type, int id) => type switch
    {
        ResourceType.Labor         => await db.LaborResources.Where(r => r.Id == id).Select(r => (decimal?)r.RatePerHour).FirstOrDefaultAsync(),
        ResourceType.Material      => await db.MaterialResources.Where(r => r.Id == id).Select(r => (decimal?)r.UnitPrice).FirstOrDefaultAsync(),
        ResourceType.Equipment     => await db.EquipmentResources.Where(r => r.Id == id).Select(r => (decimal?)r.RatePerHour).FirstOrDefaultAsync(),
        ResourceType.Subcontractor => await db.Subcontractors.Where(r => r.Id == id).Select(r => (decimal?)r.UnitRate).FirstOrDefaultAsync(),
        _ => null,
    };

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
