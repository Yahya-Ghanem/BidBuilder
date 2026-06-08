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

        // 28.3 — `?preview=1` switches the response to a preview-friendly form so
        // the user can verify the figures in an in-app modal before clicking
        // Download. For PDFs the bytes are identical but the filename is dropped
        // so the browser shows it inline in an iframe (Content-Disposition
        // defaults to inline when no name is given). For xlsx/csv the response
        // becomes a self-contained HTML rendering of the same ExportModel (the
        // browser cannot natively render xlsx) — the actual deliverable file is
        // still produced by the same endpoint without the flag. Per-line
        // authorization is unchanged; the preview path goes through Export()
        // exactly like the download path so a user who can't access the
        // estimate can't read its data via ?preview=1 either.
        grp.MapGet("/{id:int}/export.xlsx", (int id, string? preview, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
            {
                if (IsPreview(preview)) return Results.File(export.BuildHtml(m), "text/html; charset=utf-8");
                var bytes = export.BuildExcel(m);
                return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{m.ProjectCode}-estimate.xlsx");
            }));

        grp.MapGet("/{id:int}/export.pdf", (int id, string? preview, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
            {
                var bytes = export.BuildPdf(m);
                // Preview omits the filename → browser renders inline.
                return IsPreview(preview)
                    ? Results.File(bytes, "application/pdf")
                    : Results.File(bytes, "application/pdf", $"{m.ProjectCode}-estimate.pdf");
            }));

        grp.MapGet("/{id:int}/export.csv", (int id, string? preview, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
            {
                if (IsPreview(preview)) return Results.File(export.BuildHtml(m), "text/html; charset=utf-8");
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

        // ── Bid submission letter ───────────────────────────────────────────
        grp.MapGet("/{id:int}/bid-letter.pdf", (int id, string? to, string? toTitle, string? from, string? fromTitle, int? validityDays, string? note, string? reference,
            ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
            {
                var opts = new BidLetterOptions(
                    Recipient: to, RecipientTitle: toTitle,
                    // Default the signatory to the signed-in user's name.
                    Signatory: string.IsNullOrWhiteSpace(from) ? me.Name() : from,
                    SignatoryTitle: fromTitle, ValidityDays: validityDays, Note: note, Reference: reference);
                return Results.File(export.BuildBidLetterPdf(m, opts), "application/pdf", $"{m.ProjectCode}-BidLetter.pdf");
            }));

        // ── Cost by area (level = detail|area|subarea|unit) ─────────────────
        grp.MapGet("/{id:int}/cost-by-area.xlsx", (int id, string? level, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
                Results.File(export.BuildCostByAreaExcel(m, level ?? "detail"), Xlsx, CostByAreaFile(level, "xlsx"))));

        grp.MapGet("/{id:int}/cost-by-area.csv", (int id, string? level, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
                Results.File(export.BuildCostByAreaCsv(m, level ?? "detail"), "text/csv", CostByAreaFile(level, "csv"))));

        grp.MapGet("/{id:int}/cost-by-area.pdf", (int id, string? level, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AreaRollupService rollup, ExportService export, ITenantContext tc) =>
            Export(id, me, access, perm, db, calc, rollup, tc, async m =>
                Results.File(export.BuildCostByAreaPdf(m, level ?? "detail"), "application/pdf", CostByAreaFile(level, "pdf"))));
    }

    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// 28.3 — Accept both <c>?preview=1</c> (per the roadmap) and <c>?preview=true</c>
    /// (per ASP.NET Core's default bool binding). String binding is used instead of
    /// <c>bool?</c> because the latter rejects "1" with a 400, which would break the
    /// documented contract.
    /// </summary>
    private static bool IsPreview(string? preview) =>
        preview is not null && (preview == "1" || preview.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Download filename for the activities export, reflecting the grouping level.</summary>
    private static string ActivitiesFile(string? level, string ext) =>
        (level?.Trim().ToLowerInvariant()) switch
        {
            "area"                  => $"ActivitiesByArea.{ext}",
            "subarea" or "sub-area" => $"ActivitiesBySubArea.{ext}",
            "unit"                  => $"ActivitiesByUnitSummary.{ext}",
            _                       => $"ActivitiesByUnit.{ext}",
        };

    private static string CostByAreaFile(string? level, string ext) =>
        (level?.Trim().ToLowerInvariant()) switch
        {
            "area"                  => $"CostByArea.{ext}",
            "subarea" or "sub-area" => $"CostBySubArea.{ext}",
            "unit"                  => $"CostByUnit.{ext}",
            _                       => $"CostByArea.{ext}",
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
            areaRollup is { Areas.Count: > 0 } ? areaRollup : null,
            // 24.5 — branding text (header/footer/signature) plumbed through to the
            // PDF renderer; nulls leave the legacy layout intact.
            settings?.BrandHeaderText, settings?.BrandFooterText, settings?.BrandSignatureText);

        return await render(model);
    }
}
