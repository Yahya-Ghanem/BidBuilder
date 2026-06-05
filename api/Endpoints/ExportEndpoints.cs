using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

/// <summary>
/// Download an estimate as a priced-BOQ + bid-summary (Excel / PDF). Gated by
/// project access + the <c>reports</c> module permission.
/// </summary>
public static class ExportEndpoints
{
    private const string Mod = "reports";

    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/estimates").RequireAuthorization();

        grp.MapGet("/{id:int}/export.xlsx", (int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
            {
                var bytes = export.BuildExcel(m);
                return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{m.ProjectCode}-estimate.xlsx");
            }));

        grp.MapGet("/{id:int}/export.pdf", (int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
            {
                var bytes = export.BuildPdf(m);
                return Results.File(bytes, "application/pdf", $"{m.ProjectCode}-estimate.pdf");
            }));

        grp.MapGet("/{id:int}/export.csv", (int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
            {
                var bytes = export.BuildCsv(m);
                return Results.File(bytes, "text/csv", $"{m.ProjectCode}-boq.csv");
            }));

        // ── Activities by unit / area / sub-area (level = detail|area|subarea) ──
        grp.MapGet("/{id:int}/activities.xlsx", (int id, string? level, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
                Results.File(export.BuildActivitiesExcel(m, level ?? "detail"), Xlsx, ActivitiesFile(level, "xlsx"))));

        grp.MapGet("/{id:int}/activities.csv", (int id, string? level, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
                Results.File(export.BuildActivitiesCsv(m, level ?? "detail"), "text/csv", ActivitiesFile(level, "csv"))));

        grp.MapGet("/{id:int}/activities.pdf", (int id, string? level, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
                Results.File(export.BuildActivitiesPdf(m, level ?? "detail"), "application/pdf", ActivitiesFile(level, "pdf"))));

        // ── Cost by area ────────────────────────────────────────────────────
        grp.MapGet("/{id:int}/cost-by-area.xlsx", (int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
                Results.File(export.BuildCostByAreaExcel(m), Xlsx, "CostByArea.xlsx")));

        grp.MapGet("/{id:int}/cost-by-area.csv", (int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
                Results.File(export.BuildCostByAreaCsv(m), "text/csv", "CostByArea.csv")));

        grp.MapGet("/{id:int}/cost-by-area.pdf", (int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
                Results.File(export.BuildCostByAreaPdf(m), "application/pdf", "CostByArea.pdf")));
    }

    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Download filename for the activities export, reflecting the grouping level.</summary>
    private static string ActivitiesFile(string? level, string ext) =>
        (level?.Trim().ToLowerInvariant()) switch
        {
            "area"                  => $"ActivitiesByArea.{ext}",
            "subarea" or "sub-area" => $"ActivitiesBySubArea.{ext}",
            _                       => $"ActivitiesByUnit.{ext}",
        };

    private static async Task<IResult> Export(
        int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm,
        AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ITenantContext tc, Func<ExportModel, Task<IResult>> render)
    {
        if (!await access.CanAccessEstimateAsync(me, id))
            return Results.NotFound(new { error = "Estimate not found or not accessible" });
        if (!await perm.CanAsync(me, Mod, ModuleAction.View))
            return Results.Json(new { error = "Missing 'View' permission on reports" }, statusCode: 403);

        var bd = await calc.GetAsync(id);
        if (bd is null) return Results.NotFound(new { error = "Estimate not found" });

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == bd.ProjectId);
        if (project is null) return Results.NotFound(new { error = "Project not found" });

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tc.TenantId);
        var settings = await db.TenantSettings.FirstOrDefaultAsync();

        // Company branding lines for the document header (omit blanks).
        var address = settings is null ? null
            : string.Join(", ", new[] { settings.Address, settings.City, settings.Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var contact = settings is null ? null
            : string.Join("   ·   ", new[] { settings.Phone, settings.ContactEmail, settings.Website }.Where(x => !string.IsNullOrWhiteSpace(x)));

        var areaRollup = await rollup.ComputeAsync(id);

        var model = new ExportModel(
            tenant?.Name ?? "BidBuilder",
            string.IsNullOrWhiteSpace(address) ? null : address,
            string.IsNullOrWhiteSpace(contact) ? null : contact,
            project.Code, project.Name, project.ClientName, project.Location,
            DateTime.UtcNow.ToString("yyyy-MM-dd"), bd,
            settings?.LogoBytes is { Length: > 0 } ? settings.LogoBytes : null,
            areaRollup is { Areas.Count: > 0 } ? areaRollup : null);

        return await render(model);
    }
}
