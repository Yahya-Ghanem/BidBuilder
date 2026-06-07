using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────
public record EstimateTemplateDto(int Id, string Name, string? Description, int SectionCount, int ItemCount, DateTime CreatedAt);
public record SaveTemplateInput(string? Name, string? Description, int EstimateId);
public record FromTemplateInput(int TemplateId, string? Title);

/// <summary>
/// 21.3 — Reusable estimate templates. Save an estimate's structure as a tenant
/// template, then spin up a new Draft estimate from it in any project. Gated by the
/// <c>estimate-admin</c> module (same as creating/cloning estimates) plus project
/// access for the source/target project.
/// </summary>
public static class EstimateTemplateEndpoints
{
    private const string Admin = "estimate-admin";

    public static void MapEstimateTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/estimate-templates").RequireAuthorization();

        // List the tenant's templates (anyone who can create estimates picks from here).
        grp.MapGet("/", async (ClaimsPrincipal me, AppDbContext db, PermissionService perm) =>
        {
            if (!await perm.CanAsync(me, Admin, ModuleAction.View)) return Forbid();
            var rows = await db.EstimateTemplates.OrderByDescending(t => t.Id)
                .Select(t => new EstimateTemplateDto(t.Id, t.Name, t.Description, t.SectionCount, t.ItemCount, t.CreatedAt))
                .ToListAsync();
            return Results.Ok(rows);
        });

        // Save an estimate's structure as a template.
        grp.MapPost("/", async (SaveTemplateInput i, ClaimsPrincipal me, AppDbContext db,
            PermissionService perm, ProjectAccessService access, EstimateTemplateService templates, AuditService audit) =>
        {
            if (!await perm.CanAsync(me, Admin, ModuleAction.Add)) return Forbid();
            var name = i.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return Bad("Name is required.");
            if (name!.Length > 160) return Bad("Name is too long (max 160).");

            var est = await db.Estimates
                .Include(e => e.Sections).ThenInclude(s => s.Items).ThenInclude(it => it.CostComponents)
                .Include(e => e.Preliminaries)
                .Include(e => e.Markups)
                .Include(e => e.Risks)
                .FirstOrDefaultAsync(e => e.Id == i.EstimateId);
            if (est is null) return Results.NotFound(new { error = "Estimate not found" });
            if (!await access.CanAccessProjectAsync(me, est.ProjectId))
                return Results.NotFound(new { error = "Estimate not found" });   // hide cross-project

            var payload = templates.Build(est);
            var (sections, items) = EstimateTemplateService.Counts(payload);
            var tpl = new EstimateTemplate
            {
                Name = name, Description = i.Description?.Trim(),
                PayloadJson = EstimateTemplateService.Serialize(payload),
                SectionCount = sections, ItemCount = items,
                CreatedByUserId = me.Id(),
            };
            db.EstimateTemplates.Add(tpl);                 // TenantId auto-stamped
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "estimate-template.create", "EstimateTemplate", tpl.Id.ToString(),
                $"{name} ({sections} sections / {items} items) from estimate {est.Id}");
            return Results.Created($"/api/estimate-templates/{tpl.Id}",
                new EstimateTemplateDto(tpl.Id, tpl.Name, tpl.Description, tpl.SectionCount, tpl.ItemCount, tpl.CreatedAt));
        });

        // Delete a template.
        grp.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db, PermissionService perm, AuditService audit) =>
        {
            if (!await perm.CanAsync(me, Admin, ModuleAction.Delete)) return Forbid();
            var tpl = await db.EstimateTemplates.FirstOrDefaultAsync(t => t.Id == id);
            if (tpl is null) return Results.NotFound(new { error = "Template not found" });
            db.EstimateTemplates.Remove(tpl);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "estimate-template.delete", "EstimateTemplate", id.ToString(), tpl.Name);
            return Results.NoContent();
        });

        // Create a new estimate in a project FROM a template.
        app.MapPost("/api/projects/{projectId:int}/estimates/from-template",
            async (int projectId, FromTemplateInput i, ClaimsPrincipal me, AppDbContext db,
                PermissionService perm, ProjectAccessService access, EstimateTemplateService templates, AuditService audit) =>
        {
            if (!await access.CanAccessProjectAsync(me, projectId))
                return Results.NotFound(new { error = "Project not found or not accessible" });
            if (!await perm.CanAsync(me, Admin, ModuleAction.Add))
                return Results.Json(new { error = "Missing 'Add' permission on estimate-admin" }, statusCode: 403);

            var tpl = await db.EstimateTemplates.FirstOrDefaultAsync(t => t.Id == i.TemplateId);
            if (tpl is null) return Results.NotFound(new { error = "Template not found" });

            var est = await templates.ApplyAsync(projectId, i.Title, tpl);
            await audit.LogAsync(me, "estimate.from-template", "Estimate", est.Id.ToString(),
                $"rev {est.Revision} from template {tpl.Id} ({tpl.Name})");
            return Results.Created($"/api/estimates/{est.Id}",
                new EstimateSummaryDto(est.Id, est.Revision, est.Title, est.Status.ToString(), est.Currency, est.BidPrice, est.UpdatedAt));
        }).RequireAuthorization();
    }

    private static IResult Bad(string msg) => Results.BadRequest(new { error = msg });
    private static IResult Forbid() => Results.Json(new { error = "You don't have access to estimate templates." }, statusCode: 403);
}
