using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────
public record EstimateTemplateDto(int Id, string Name, string? Description, int SectionCount, int ItemCount,
    DateTime CreatedAt, string? Category, string[] Tags, bool IsFeatured, string? CreatedByName);
public record SaveTemplateInput(string? Name, string? Description, int EstimateId, string? Category, string[]? Tags);
/// <summary>22.3 — edit a template's library metadata (not its captured structure).</summary>
public record UpdateTemplateInput(string? Name, string? Description, string? Category, string[]? Tags);
/// <summary>22.3 — pin/unpin a template for the org.</summary>
public record FeatureTemplateInput(bool Featured);
public record FromTemplateInput(int TemplateId, string? Title);

/// <summary>
/// 24.3 — Portable JSON envelope for cross-tenant template sharing. A tenant exports
/// one of their templates as JSON (export.json), hands the file to another tenant's
/// admin, who uploads it via /import to land a fresh tenant-owned copy. The
/// <see cref="SchemaVersion"/> field is the migration knob if the captured payload
/// shape evolves; currently <c>1</c>.
/// </summary>
public record TemplateExportEnvelope(
    int SchemaVersion,
    string Name,
    string? Description,
    string? Category,
    string[]? Tags,
    JsonElement Payload);

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

        // List the tenant's templates as a browsable library: optional ?search (name/
        // description), ?category (exact), ?tag (exact membership) filters; featured pinned
        // to the top, then newest first. Includes the author's name for the card.
        grp.MapGet("/", async (ClaimsPrincipal me, AppDbContext db, PermissionService perm,
            string? search, string? category, string? tag) =>
        {
            if (!await perm.CanAsync(me, Admin, ModuleAction.View)) return Forbid();

            var q = db.EstimateTemplates.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var pat = $"%{search.Trim()}%";
                q = q.Where(t => EF.Functions.ILike(t.Name, pat)
                              || (t.Description != null && EF.Functions.ILike(t.Description, pat)));
            }
            if (!string.IsNullOrWhiteSpace(category))
            {
                var cat = category.Trim();
                q = q.Where(t => t.Category != null && t.Category.ToLower() == cat.ToLower());
            }

            var raw = await (from t in q
                             join u in db.Users on t.CreatedByUserId equals u.Id into uj
                             from u in uj.DefaultIfEmpty()
                             orderby t.IsFeatured descending, t.Id descending
                             select new { t, CreatedByName = (string?)(u != null ? u.Name : null) })
                            .ToListAsync();

            // Tag membership is exact (not substring), so split the stored CSV in memory.
            var rows = raw.Select(x => ToDto(x.t, x.CreatedByName));
            if (!string.IsNullOrWhiteSpace(tag))
            {
                var want = tag.Trim();
                rows = rows.Where(d => d.Tags.Any(g => string.Equals(g, want, StringComparison.OrdinalIgnoreCase)));
            }
            return Results.Ok(rows.ToList());
        });

        // Distinct categories in use (for the library filter dropdown).
        grp.MapGet("/categories", async (ClaimsPrincipal me, AppDbContext db, PermissionService perm) =>
        {
            if (!await perm.CanAsync(me, Admin, ModuleAction.View)) return Forbid();
            var cats = await db.EstimateTemplates
                .Where(t => t.Category != null && t.Category != "")
                .Select(t => t.Category!).Distinct().OrderBy(c => c).ToListAsync();
            return Results.Ok(cats);
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

            if (i.Category is { } cat && cat.Trim().Length > 40) return Bad("Category is too long (max 40).");

            var payload = templates.Build(est);
            var (sections, items) = EstimateTemplateService.Counts(payload);
            var tpl = new EstimateTemplate
            {
                Name = name, Description = i.Description?.Trim(),
                PayloadJson = EstimateTemplateService.Serialize(payload),
                SectionCount = sections, ItemCount = items,
                Category = Clean(i.Category), Tags = NormalizeTags(i.Tags),
                CreatedByUserId = me.Id(),
            };
            db.EstimateTemplates.Add(tpl);                 // TenantId auto-stamped
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "estimate-template.create", "EstimateTemplate", tpl.Id.ToString(),
                $"{name} ({sections} sections / {items} items) from estimate {est.Id}");
            return Results.Created($"/api/estimate-templates/{tpl.Id}", ToDto(tpl, me.Name()));
        });

        // Edit a template's library metadata (name / description / category / tags).
        grp.MapPut("/{id:int}", async (int id, UpdateTemplateInput i, ClaimsPrincipal me, AppDbContext db, PermissionService perm, AuditService audit) =>
        {
            if (!await perm.CanAsync(me, Admin, ModuleAction.Edit)) return Forbid();
            var name = i.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return Bad("Name is required.");
            if (name!.Length > 160) return Bad("Name is too long (max 160).");
            if (i.Category is { } cat && cat.Trim().Length > 40) return Bad("Category is too long (max 40).");

            var tpl = await db.EstimateTemplates.FirstOrDefaultAsync(t => t.Id == id);
            if (tpl is null) return Results.NotFound(new { error = "Template not found" });
            tpl.Name = name; tpl.Description = Clean(i.Description);
            tpl.Category = Clean(i.Category); tpl.Tags = NormalizeTags(i.Tags);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "estimate-template.update", "EstimateTemplate", id.ToString(), name);
            return Results.Ok(ToDto(tpl, me.Name()));
        });

        // Pin / unpin a template for the org. Tenant admin only — "feature for your org".
        grp.MapPut("/{id:int}/featured", async (int id, FeatureTemplateInput i, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can feature templates." }, statusCode: 403);
            var tpl = await db.EstimateTemplates.FirstOrDefaultAsync(t => t.Id == id);
            if (tpl is null) return Results.NotFound(new { error = "Template not found" });
            tpl.IsFeatured = i.Featured;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, i.Featured ? "estimate-template.featured" : "estimate-template.unfeatured",
                "EstimateTemplate", id.ToString(), tpl.Name);
            return Results.Ok(ToDto(tpl, me.Name()));
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

        // 24.3 — Export a template as a portable JSON envelope. Re-scoped from the
        // "starter library with nullable TenantId" IOU: rather than touch IHasTenant /
        // global query filter / auto-stamp machinery, we ship cross-tenant sharing
        // via an export/import pair. The envelope is self-contained — name,
        // description, library metadata, and the captured payload — and the
        // schemaVersion lets us migrate older files if the payload shape evolves.
        grp.MapGet("/{id:int}/export.json", async (int id, ClaimsPrincipal me, AppDbContext db, PermissionService perm) =>
        {
            if (!await perm.CanAsync(me, Admin, ModuleAction.View)) return Forbid();
            var tpl = await db.EstimateTemplates.FirstOrDefaultAsync(t => t.Id == id);
            if (tpl is null) return Results.NotFound(new { error = "Template not found" });

            // The stored PayloadJson is already a JSON document — embed it as JsonElement
            // (not a quoted string) so the consumer sees structured JSON, not escaped text.
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(tpl.PayloadJson) ? "{}" : tpl.PayloadJson);
            var envelope = new TemplateExportEnvelope(
                SchemaVersion: 1,
                Name:          tpl.Name,
                Description:   tpl.Description,
                Category:      tpl.Category,
                Tags:          TagArray(tpl.Tags),
                Payload:       doc.RootElement.Clone());

            // Web defaults → camelCase property names (matching how the rest of the API
            // serialises), and indented for human-legible files.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            // Sanitize the filename: strip control chars, collapse to safe chars.
            var safe  = string.Concat(tpl.Name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' '));
            var fname = string.IsNullOrWhiteSpace(safe) ? $"template-{id}.json" : $"{safe.Trim().Replace(' ', '-')}.json";
            return Results.File(bytes, "application/json", fname);
        });

        // 24.3 — Import a previously-exported envelope into the current tenant as a
        // fresh template (always tenant-owned by the caller; never linked back to the
        // source). Accepts JSON in the request body (so curl / fetch work) — drop the
        // file picker on the client to read-as-text then POST.
        grp.MapPost("/import", async (TemplateExportEnvelope body, ClaimsPrincipal me, AppDbContext db,
            PermissionService perm, AuditService audit) =>
        {
            if (!await perm.CanAsync(me, Admin, ModuleAction.Add)) return Forbid();
            if (body.SchemaVersion != 1)
                return Bad($"Unsupported schemaVersion {body.SchemaVersion} (this server understands 1).");
            var name = body.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return Bad("Name is required.");
            if (name!.Length > 160) return Bad("Name is too long (max 160).");
            if (body.Category is { } cat && cat.Trim().Length > 40) return Bad("Category is too long (max 40).");
            if (body.Payload.ValueKind != JsonValueKind.Object)
                return Bad("Payload is required and must be a JSON object.");

            var payloadJson = body.Payload.GetRawText();
            // Parse the payload to recover the section/item counts for the list view
            // (we never trust whatever the file claimed — re-derive from the structure).
            var (sections, items) = EstimateTemplateService.CountsFromJson(payloadJson);

            var tpl = new EstimateTemplate
            {
                Name = name,
                Description = Clean(body.Description),
                PayloadJson = payloadJson,
                SectionCount = sections,
                ItemCount = items,
                Category = Clean(body.Category),
                Tags = NormalizeTags(body.Tags),
                CreatedByUserId = me.Id(),
            };
            db.EstimateTemplates.Add(tpl);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "estimate-template.import", "EstimateTemplate", tpl.Id.ToString(),
                $"imported \"{name}\" ({sections} sections / {items} items)");
            return Results.Created($"/api/estimate-templates/{tpl.Id}", ToDto(tpl, me.Name()));
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

    // ── helpers ──────────────────────────────────────────────────────────────────
    private static EstimateTemplateDto ToDto(EstimateTemplate t, string? createdByName) => new(
        t.Id, t.Name, t.Description, t.SectionCount, t.ItemCount, t.CreatedAt,
        t.Category, TagArray(t.Tags), t.IsFeatured, createdByName);

    private static string[] TagArray(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Trim, drop blanks, dedupe (case-insensitive), cap to 12 tags of ≤30 chars,
    /// store comma-separated. Null when empty.</summary>
    private static string? NormalizeTags(string[]? tags)
    {
        if (tags is null) return null;
        var clean = tags.Select(t => t.Trim())
            .Where(t => t.Length is > 0 and <= 30)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        return clean.Count == 0 ? null : string.Join(",", clean);
    }

    private static IResult Bad(string msg) => Results.BadRequest(new { error = msg });
    private static IResult Forbid() => Results.Json(new { error = "You don't have access to estimate templates." }, statusCode: 403);
}
