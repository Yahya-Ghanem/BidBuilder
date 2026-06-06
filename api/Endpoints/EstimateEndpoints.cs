using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

public record EstimateSummaryDto(int Id, int Revision, string Title, string Status, string Currency, decimal BidPrice, DateTime UpdatedAt);
public record CreateEstimateRequest(string? Title);
public record UpdateEstimateRequest(string? Title, string? Status, string? SecondaryCurrency, decimal? TaxRatePct);
public record CopyEstimateRequest(int TargetProjectId, string? Title);
public record SectionInput(string Code, string Title, int SortOrder, int? ParentSectionId);
public record ItemInput(string? ItemCode, string Description, string Unit, decimal Quantity, int? AssemblyId, decimal UnitRate, int SortOrder, List<ItemCostComponentInput>? Components, int? AreaId, string? Kind = null);
public record ItemCostComponentInput(int TypeId, decimal Value, decimal? Quantity = null, decimal? Rate = null);
public record CloneRoomInput(string? Name);
public record PrelimInput(string Description, string Kind, decimal Amount, int SortOrder);
public record MarkupInput(string Type, string? Label, decimal Percentage, int ApplyOrder);
public record WhatIfRequest(List<MarkupInput> Markups);
public record ImportResultDto(int SectionsAdded, int ItemsAdded, EstimateBreakdown Estimate);

/// <summary>
/// Estimate editing: BOQ sections/items, preliminaries, markups, and the
/// recompute → bid price roll-up. Every route requires access to the owning
/// project (the scoping layer) AND the relevant module permission. Each mutation
/// recomputes and returns the full breakdown so the client always has fresh totals.
/// </summary>
public static class EstimateEndpoints
{
    private const string Boq      = "boq";
    private const string Prelims  = "prelims-markups";
    private const string Admin    = "estimate-admin";

    public static void MapEstimateEndpoints(this IEndpointRouteBuilder app)
    {
        // Estimates under a project.
        var proj = app.MapGroup("/api/projects").RequireAuthorization();

        proj.MapGet("/{projectId:int}/estimates", async (int projectId, ClaimsPrincipal me, ProjectAccessService access, AppDbContext db) =>
        {
            if (!await access.CanAccessProjectAsync(me, projectId))
                return Results.NotFound(new { error = "Project not found or not accessible" });
            var list = await db.Estimates.Where(e => e.ProjectId == projectId)
                .OrderBy(e => e.Revision)
                .Select(e => new EstimateSummaryDto(e.Id, e.Revision, e.Title, e.Status.ToString(), e.Currency, e.BidPrice, e.UpdatedAt))
                .ToListAsync();
            return Results.Ok(list);
        });

        // POST a blank new estimate (revision) under a project. Gated estimate-admin Add.
        proj.MapPost("/{projectId:int}/estimates", async (int projectId, CreateEstimateRequest req, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AuditService audit) =>
        {
            if (!await access.CanAccessProjectAsync(me, projectId))
                return Results.NotFound(new { error = "Project not found or not accessible" });
            if (!await perm.CanAsync(me, Admin, ModuleAction.Add))
                return Results.Json(new { error = "Missing 'Add' permission on estimate-admin" }, statusCode: 403);

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
            if (project is null) return NotFound();

            var nextRev = ((await db.Estimates.Where(e => e.ProjectId == projectId).MaxAsync(e => (int?)e.Revision)) ?? 0) + 1;
            // Pre-fill the VAT/tax rate from the tenant default (null when none configured).
            var defaultTax = await db.TenantSettings.Select(s => s.DefaultTaxRatePct).FirstOrDefaultAsync();
            var est = new Estimate
            {
                ProjectId = projectId,
                Revision  = nextRev,
                Title     = string.IsNullOrWhiteSpace(req.Title) ? $"Revision {nextRev}" : req.Title!.Trim(),
                Status    = EstimateStatus.Draft,
                Currency  = project.Currency,
                TaxRatePct = defaultTax > 0m ? defaultTax : null,
            };
            db.Estimates.Add(est);
            await db.SaveChangesAsync();
            await calc.RecomputeAsync(est.Id);
            await audit.LogAsync(me, "estimate.create", "Estimate", est.Id.ToString(), $"{est.Title} (rev {est.Revision}), project {projectId}");
            return Results.Created($"/api/estimates/{est.Id}", Summ(est));
        });

        // POST clone an estimate into the next revision (deep copy, status reset to Draft).
        proj.MapPost("/{projectId:int}/estimates/{id:int}/clone", async (int projectId, int id, CreateEstimateRequest req, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AuditService audit) =>
        {
            if (!await access.CanAccessProjectAsync(me, projectId))
                return Results.NotFound(new { error = "Project not found or not accessible" });
            if (!await perm.CanAsync(me, Admin, ModuleAction.Add))
                return Results.Json(new { error = "Missing 'Add' permission on estimate-admin" }, statusCode: 403);

            var src = await db.Estimates
                .Include(e => e.Sections).ThenInclude(s => s.Items).ThenInclude(i => i.CostComponents)
                .Include(e => e.Preliminaries)
                .Include(e => e.Markups)
                .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId);
            if (src is null) return NotFound();

            var clone = await DeepCopyEstimateAsync(db, calc, src, projectId, req.Title);
            await audit.LogAsync(me, "estimate.clone", "Estimate", clone.Id.ToString(), $"rev {clone.Revision} from estimate {id}");
            return Results.Created($"/api/estimates/{clone.Id}", Summ(clone));
        });

        // POST copy an estimate into ANOTHER project as a new revision. Needs access
        // to the source project + estimate-admin Add on the target project.
        proj.MapPost("/{projectId:int}/estimates/{id:int}/copy", async (int projectId, int id, CopyEstimateRequest req, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AuditService audit) =>
        {
            if (!await access.CanAccessProjectAsync(me, projectId))
                return Results.NotFound(new { error = "Source project not found or not accessible" });
            if (req.TargetProjectId == projectId)
                return Bad("Target project must differ from the source (use Duplicate for same-project revisions).");
            if (!await access.CanAccessProjectAsync(me, req.TargetProjectId))
                return Results.NotFound(new { error = "Target project not found or not accessible" });
            if (!await perm.CanAsync(me, Admin, ModuleAction.Add))
                return Results.Json(new { error = "Missing 'Add' permission on estimate-admin" }, statusCode: 403);

            var src = await db.Estimates
                .Include(e => e.Sections).ThenInclude(s => s.Items).ThenInclude(i => i.CostComponents)
                .Include(e => e.Preliminaries)
                .Include(e => e.Markups)
                .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId);
            if (src is null) return NotFound();

            var copy = await DeepCopyEstimateAsync(db, calc, src, req.TargetProjectId, req.Title);
            await audit.LogAsync(me, "estimate.copy", "Estimate", copy.Id.ToString(), $"from estimate {id} (project {projectId}) → project {req.TargetProjectId}, rev {copy.Revision}");
            return Results.Created($"/api/estimates/{copy.Id}", Summ(copy));
        });

        // DELETE a whole estimate revision (estimate-admin Delete). Removes its BOQ
        // tree, preliminaries and markups. Children are removed explicitly so the
        // self-referencing section parent FK can't trip a cascade-ordering error.
        proj.MapDelete("/{projectId:int}/estimates/{id:int}", async (int projectId, int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, AuditService audit) =>
        {
            if (!await access.CanAccessProjectAsync(me, projectId))
                return Results.NotFound(new { error = "Project not found or not accessible" });
            if (!await perm.CanAsync(me, Admin, ModuleAction.Delete))
                return Results.Json(new { error = "Missing 'Delete' permission on estimate-admin" }, statusCode: 403);

            var e = await db.Estimates
                .Include(x => x.Sections).ThenInclude(s => s.Items)
                .Include(x => x.Preliminaries)
                .Include(x => x.Markups)
                .FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId);
            if (e is null) return NotFound();

            db.BoqItems.RemoveRange(e.Sections.SelectMany(s => s.Items));
            db.BoqSections.RemoveRange(e.Sections);
            db.Preliminaries.RemoveRange(e.Preliminaries);
            db.Markups.RemoveRange(e.Markups);
            db.Estimates.Remove(e);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "estimate.delete", "Estimate", id.ToString(), $"rev {e.Revision} from project {projectId}");
            return Results.NoContent();
        });

        var grp = app.MapGroup("/api/estimates").RequireAuthorization();

        // Optimistic concurrency: if the caller sent the row version it last saw
        // (If-Match header = the estimate's xmin), pin it on the estimate so the
        // handler's SaveChanges issues UPDATE … WHERE xmin = expected. A concurrent
        // edit advanced xmin → EF throws DbUpdateConcurrencyException → surfaced as
        // 409 instead of 500. No header (reads, or older clients) = last-writer-wins.
        grp.AddEndpointFilter(async (ctx, next) =>
        {
            var ifMatch = ctx.HttpContext.Request.Headers.IfMatch.ToString();
            if (!string.IsNullOrWhiteSpace(ifMatch)
                && ctx.HttpContext.Request.RouteValues.TryGetValue("id", out var raw)
                && int.TryParse(raw?.ToString(), out var estId))
            {
                var calc = ctx.HttpContext.RequestServices.GetRequiredService<EstimateCalculator>();
                await calc.GuardVersionAsync(estId, ifMatch);
            }
            try { return await next(ctx); }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Json(new { error = "This estimate was changed by someone else. Reload the latest and try again." }, statusCode: 409);
            }
        });

        // Status lock: a Published/Superseded revision is frozen — its content can't be
        // edited so a finalised bid can't silently change. FAIL-CLOSED: every write
        // (POST/PUT/DELETE) under /api/estimates is locked when the revision is finalised
        // UNLESS the route opts out with .AllowWhenFinalised() (the read-equivalent writes:
        // status/title PUT — the unlock path itself — plus recompute and what-if). So a
        // newly added mutating route is locked by default rather than slipping past a
        // hand-maintained path allowlist.
        grp.AddEndpointFilter(async (ctx, next) =>
        {
            var isWrite = ctx.HttpContext.Request.Method is "POST" or "PUT" or "DELETE";
            var allowed = ctx.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AllowWhenFinalisedMarker>() is not null;
            if (isWrite && !allowed
                && ctx.HttpContext.Request.RouteValues.TryGetValue("id", out var raw)
                && int.TryParse(raw?.ToString(), out var estId))
            {
                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var status = await db.Estimates.Where(e => e.Id == estId).Select(e => (EstimateStatus?)e.Status).FirstOrDefaultAsync();
                if (status is EstimateStatus.Published or EstimateStatus.Superseded)
                    return Results.Json(new { error = $"This revision is {status} and locked. Revert it to Draft to edit." }, statusCode: 409);
            }
            return await next(ctx);
        });

        // GET breakdown (cached).
        grp.MapGet("/{id:int}", async (int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.View, access, perm); if (g is not null) return g;
            var bd = await calc.GetAsync(id);
            return bd is null ? NotFound() : Results.Ok(bd);
        });

        // POST recompute (explicit refresh).
        grp.MapPost("/{id:int}/recompute", async (int id, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.View, access, perm); if (g is not null) return g;
            var bd = await calc.RecomputeAsync(id);
            return bd is null ? NotFound() : Results.Ok(bd);
        }).AllowWhenFinalised();

        // POST reconcile — recompute and report whether the cached totals have drifted
        // from a clean recomputation (the safety net for the denormalised money cache).
        // Read-only by default; persists the corrected totals only with ?commit=true.
        // A finalised (Published/Superseded) revision may be drift-CHECKED but never
        // silently rewritten, so a commit is refused on it (revert to Draft to repair).
        // estimate-admin View — a reconciliation/diagnostic tool.
        grp.MapPost("/{id:int}/reconcile", async (int id, bool? commit, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Admin, ModuleAction.View, access, perm); if (g is not null) return g;
            var doCommit = commit ?? false;
            if (doCommit)
            {
                var status = await db.Estimates.Where(e => e.Id == id).Select(e => (EstimateStatus?)e.Status).FirstOrDefaultAsync();
                if (status is EstimateStatus.Published or EstimateStatus.Superseded)
                    return Results.Json(new { error = $"This revision is {status} and locked; reconcile can report drift but cannot rewrite a finalised bid. Revert it to Draft to repair." }, statusCode: 409);
            }
            var r = await calc.ReconcileAsync(id, doCommit);
            return r is null ? NotFound() : Results.Ok(r);
        }).AllowWhenFinalised();

        // PUT estimate meta (title / lifecycle status). Gated estimate-admin Edit.
        grp.MapPut("/{id:int}", async (int id, UpdateEstimateRequest req, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, AuditService audit) =>
        {
            var g = await Guard(me, id, Admin, ModuleAction.Edit, access, perm); if (g is not null) return g;
            var e = await db.Estimates.FirstOrDefaultAsync(x => x.Id == id); if (e is null) return NotFound();
            var oldStatus = e.Status;
            var oldSecondary = e.SecondaryCurrency;
            if (!string.IsNullOrWhiteSpace(req.Title)) e.Title = req.Title!.Trim();
            if (!string.IsNullOrWhiteSpace(req.Status))
            {
                if (!Enum.TryParse<EstimateStatus>(req.Status, true, out var st)) return Bad($"Invalid status '{req.Status}'");
                e.Status = st;
                if (st == EstimateStatus.Published) e.PublishedAt ??= DateTime.UtcNow;
            }
            // Tax/VAT rate: null = unchanged; 0 = no tax line; else the % (0–100).
            var taxChanged = false;
            if (req.TaxRatePct is { } tr)
            {
                if (tr < 0m || tr > 100m) return Bad("Tax rate must be between 0 and 100.");
                var normalized = tr > 0m ? tr : (decimal?)null;
                if (e.TaxRatePct != normalized) { e.TaxRatePct = normalized; taxChanged = true; }
            }
            // SecondaryCurrency: null = unchanged, "" = clear, else set (3-letter ISO).
            if (req.SecondaryCurrency is not null)
            {
                var sc = req.SecondaryCurrency.Trim();
                if (sc.Length == 0) e.SecondaryCurrency = null;
                else if (sc.Length == 3) e.SecondaryCurrency = sc.ToUpperInvariant();
                else return Bad("Secondary currency must be a 3-letter ISO code.");
            }

            // FX freeze re-evaluation: a published bid snapshots its native→secondary
            // rate so the converted figure never drifts; Draft/UnderReview use the live
            // tenant rate (no freeze). Changing the secondary currency drops any stale snapshot.
            if (!string.Equals(oldSecondary, e.SecondaryCurrency, StringComparison.OrdinalIgnoreCase))
            { e.FxRate = null; e.FxRateAt = null; }
            if (string.IsNullOrWhiteSpace(e.SecondaryCurrency))
            { e.FxRate = null; e.FxRateAt = null; }
            else if (e.Status == EstimateStatus.Published)
            {
                if (e.FxRate is null)
                {
                    e.FxRate = await calc.CrossRateAsync(e.Currency, e.SecondaryCurrency);
                    e.FxRateAt = e.FxRate is null ? null : DateTime.UtcNow;
                }
            }
            else if (e.Status is EstimateStatus.Draft or EstimateStatus.UnderReview)
            { e.FxRate = null; e.FxRateAt = null; }

            e.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, oldStatus != e.Status ? "estimate.status" : "estimate.update", "Estimate", id.ToString(),
                oldStatus != e.Status ? $"{oldStatus} → {e.Status}" : $"edited (status {e.Status})");
            // A tax-rate change shifts TaxAmount/total — recompute; otherwise the cached breakdown stands.
            return Results.Ok(taxChanged ? await calc.RecomputeAsync(id) : await calc.GetAsync(id));
        }).AllowWhenFinalised();   // status/title change is the unlock path — must stay available when finalised

        // POST what-if — preview the bid price under a proposed markup set, no save.
        // Gated by prelims-markups View (it exposes margin figures).
        grp.MapPost("/{id:int}/whatif", async (int id, WhatIfRequest req, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Prelims, ModuleAction.View, access, perm); if (g is not null) return g;
            var markups = req.Markups ?? new List<MarkupInput>();
            foreach (var m in markups)
            {
                if (!Enum.TryParse<MarkupType>(m.Type, true, out _)) return Bad($"Invalid markup type '{m.Type}'");
                if (m.Percentage < 0) return Bad("Percentage cannot be negative");
            }
            var result = await calc.WhatIfAsync(id, markups
                .Select(m => new WhatIfMarkupInput(m.Type, m.Label, m.Percentage, m.ApplyOrder)).ToList());
            return result is null ? NotFound() : Results.Ok(result);
        }).AllowWhenFinalised();   // non-persisting preview — safe on a finalised revision

        // GET a ready-to-fill .xlsx import template (boq View).
        grp.MapGet("/import-template.xlsx", async (ClaimsPrincipal me, PermissionService perm, ImportService import) =>
        {
            if (!await perm.CanAsync(me, Boq, ModuleAction.View))
                return Results.Json(new { error = "Missing 'View' permission on boq" }, statusCode: 403);
            return Results.File(import.BuildBoqTemplate(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "boq-import-template.xlsx");
        });

        // POST import a BOQ from an .xlsx upload — appends parsed sections/items, recomputes.
        grp.MapPost("/{id:int}/import", async (int id, IFormFile? file, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc, ImportService import) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.Add, access, perm); if (g is not null) return g;
            if (file is null || file.Length == 0) return Bad("No file uploaded.");

            List<ImportSection> parsed;
            try { await using var s = file.OpenReadStream(); parsed = import.ParseBoq(s); }
            catch (ImportException ex) { return Bad(ex.Message); }

            // Resolve any assembly codes the sheet references (tenant-scoped). Matched
            // case-insensitively so a sheet listing "asm-rc-foot" resolves the library's
            // "ASM-RC-FOOT" instead of being rejected as unknown.
            var codes = parsed.SelectMany(x => x.Items).Where(i => i.AssemblyCode != null)
                              .Select(i => i.AssemblyCode!).Distinct().ToList();
            var upper = codes.Select(c => c.ToUpperInvariant()).ToHashSet();
            var assemblies = (await db.Assemblies.Where(a => upper.Contains(a.Code.ToUpper())).ToListAsync())
                              .ToDictionary(a => a.Code.ToUpperInvariant(), a => a.Id);
            var missing = codes.Where(c => !assemblies.ContainsKey(c.ToUpperInvariant())).ToList();
            if (missing.Count > 0) return Bad($"Unknown assembly code(s): {string.Join(", ", missing)}");

            // Append below any existing sections; build the graph and persist atomically.
            var baseSort = (await db.BoqSections.Where(x => x.EstimateId == id).MaxAsync(x => (int?)x.SortOrder)) ?? 0;
            int itemsAdded = 0;
            foreach (var sec in parsed)
            {
                var section = new BoqSection { EstimateId = id, Code = sec.Code, Title = sec.Title, SortOrder = ++baseSort };
                int itemSort = 0;
                foreach (var it in sec.Items)
                {
                    var asmId = it.AssemblyCode != null ? (int?)assemblies[it.AssemblyCode.ToUpperInvariant()] : null;
                    section.Items.Add(new BoqItem
                    {
                        ItemCode = it.ItemCode, Description = it.Description, Unit = it.Unit,
                        Quantity = it.Quantity, AssemblyId = asmId,
                        UnitRate = asmId is null ? it.UnitRate : 0m, SortOrder = ++itemSort,
                    });
                    itemsAdded++;
                }
                db.BoqSections.Add(section);
            }
            await db.SaveChangesAsync();
            var bd = await calc.RecomputeAsync(id);
            return Results.Ok(new ImportResultDto(parsed.Count, itemsAdded, bd!));
        }).DisableAntiforgery();

        // ── BOQ sections (module: boq) ───────────────────────────────────────
        grp.MapPost("/{id:int}/sections", async (int id, SectionInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.Add, access, perm); if (g is not null) return g;
            if (string.IsNullOrWhiteSpace(i.Title)) return Bad("Title is required");
            db.BoqSections.Add(new BoqSection { EstimateId = id, Code = i.Code ?? "", Title = i.Title.Trim(), SortOrder = i.SortOrder, ParentSectionId = i.ParentSectionId });
            await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        grp.MapPut("/{id:int}/sections/{sid:int}", async (int id, int sid, SectionInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.Edit, access, perm); if (g is not null) return g;
            var s = await db.BoqSections.FirstOrDefaultAsync(x => x.Id == sid && x.EstimateId == id); if (s is null) return NotFound();
            s.Code = i.Code ?? ""; s.Title = i.Title.Trim(); s.SortOrder = i.SortOrder; s.ParentSectionId = i.ParentSectionId;
            await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        grp.MapDelete("/{id:int}/sections/{sid:int}", async (int id, int sid, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.Delete, access, perm); if (g is not null) return g;
            var s = await db.BoqSections.FirstOrDefaultAsync(x => x.Id == sid && x.EstimateId == id); if (s is null) return NotFound();
            db.BoqSections.Remove(s); await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        // ── BOQ items (module: boq) ──────────────────────────────────────────
        grp.MapPost("/{id:int}/sections/{sid:int}/items", async (int id, int sid, ItemInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.Add, access, perm); if (g is not null) return g;
            if (!await db.BoqSections.AnyAsync(s => s.Id == sid && s.EstimateId == id)) return NotFound();
            if (string.IsNullOrWhiteSpace(i.Description)) return Bad("Description is required");
            if (i.Quantity < 0) return Bad("Quantity cannot be negative");
            if (i.AssemblyId is not null && !await db.Assemblies.AnyAsync(a => a.Id == i.AssemblyId)) return Bad("Assembly not found");
            var compErr = await ValidateComponents(db, i.Components); if (compErr is not null) return compErr;
            var areaErr = await ValidateAreaAsync(db, id, i.AreaId); if (areaErr is not null) return areaErr;
            if (!ParseKind(i.Kind, out var kind)) return Bad($"Invalid item kind '{i.Kind}'");
            var hasComps = i.Components is { Count: > 0 };
            var item = new BoqItem
            {
                SectionId = sid, ItemCode = i.ItemCode ?? "", Description = i.Description.Trim(),
                Unit = i.Unit ?? "", Quantity = i.Quantity, AssemblyId = i.AssemblyId, AreaId = i.AreaId, Kind = kind,
                // Component build-up or assembly → rate is engine-derived; else ad-hoc.
                UnitRate = hasComps || i.AssemblyId is not null ? 0m : i.UnitRate, SortOrder = i.SortOrder,
            };
            if (hasComps)
                foreach (var c in await BuildComponentsAsync(db, i.Components!)) item.CostComponents.Add(c);
            db.BoqItems.Add(item);
            await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        grp.MapPut("/{id:int}/items/{iid:int}", async (int id, int iid, ItemInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.Edit, access, perm); if (g is not null) return g;
            var item = await db.BoqItems.Include(x => x.Section).FirstOrDefaultAsync(x => x.Id == iid && x.Section.EstimateId == id); if (item is null) return NotFound();
            if (i.Quantity < 0) return Bad("Quantity cannot be negative");
            if (i.AssemblyId is not null && !await db.Assemblies.AnyAsync(a => a.Id == i.AssemblyId)) return Bad("Assembly not found");
            var compErr = await ValidateComponents(db, i.Components); if (compErr is not null) return compErr;
            var areaErr = await ValidateAreaAsync(db, id, i.AreaId); if (areaErr is not null) return areaErr;
            // Kind absent (null) = keep the current kind — so editors that don't surface it
            // (e.g. the activities panel) can't silently reset a provisional/alternate line.
            if (i.Kind is not null)
            {
                if (!ParseKind(i.Kind, out var k)) return Bad($"Invalid item kind '{i.Kind}'");
                item.Kind = k;
            }
            item.ItemCode = i.ItemCode ?? ""; item.Description = i.Description.Trim(); item.Unit = i.Unit ?? "";
            item.Quantity = i.Quantity; item.AssemblyId = i.AssemblyId; item.AreaId = i.AreaId; item.SortOrder = i.SortOrder;

            // Replace the cost-component lines wholesale.
            var existing = await db.ItemCostComponents.Where(c => c.BoqItemId == iid).ToListAsync();
            db.ItemCostComponents.RemoveRange(existing);
            var hasComps = i.Components is { Count: > 0 };
            if (hasComps)
                foreach (var c in await BuildComponentsAsync(db, i.Components!)) { c.BoqItemId = iid; db.ItemCostComponents.Add(c); }
            // Rate is engine-derived when components or an assembly drive it; else ad-hoc.
            if (!hasComps && i.AssemblyId is null) item.UnitRate = i.UnitRate;
            await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        grp.MapDelete("/{id:int}/items/{iid:int}", async (int id, int iid, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.Delete, access, perm); if (g is not null) return g;
            var item = await db.BoqItems.Include(x => x.Section).FirstOrDefaultAsync(x => x.Id == iid && x.Section.EstimateId == id); if (item is null) return NotFound();
            db.BoqItems.Remove(item); await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        // ── Clone a room (unit area + its activities in this estimate) ────────
        grp.MapPost("/{id:int}/areas/{areaId:int}/clone", async (int id, int areaId, CloneRoomInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Boq, ModuleAction.Add, access, perm); if (g is not null) return g;
            // The status lock is enforced by the group's fail-closed endpoint filter
            // (this route is a write and is NOT marked .AllowWhenFinalised()).
            var est = await db.Estimates.Where(e => e.Id == id).Select(e => new { e.ProjectId, e.Status }).FirstOrDefaultAsync();
            if (est is null) return NotFound();
            var src = await db.Areas.FirstOrDefaultAsync(a => a.Id == areaId && a.ProjectId == est.ProjectId);
            if (src is null) return NotFound();
            var name = (i.Name ?? "").Trim();
            if (name.Length == 0) return Bad("New name is required.");

            // Clone the whole subtree rooted at the source area (the source renamed, then
            // every descendant — sub-areas and units — preserving the hierarchy). The root
            // takes the given name; descendants keep theirs. old→new area-id map.
            var all = await db.Areas.Where(a => a.ProjectId == est.ProjectId).ToListAsync();
            var childrenByParent = all.Where(a => a.ParentAreaId != null)
                .GroupBy(a => a.ParentAreaId!.Value).ToDictionary(grp2 => grp2.Key, grp2 => grp2.ToList());

            // All-or-nothing: cloning the subtree spans many level-by-level SaveChanges
            // calls plus the activity duplication. One transaction means a mid-clone
            // failure can't leave a malformed half-built area subtree behind. Run through
            // the execution strategy so the unit retries together on a transient fault.
            var strategy = db.Database.CreateExecutionStrategy();
            var result = await strategy.ExecuteAsync(async () =>
            {
            await using var tx = await db.Database.BeginTransactionAsync();

            var map = new Dictionary<int, int>();
            var root = new Area
            {
                ProjectId = src.ProjectId, ParentAreaId = src.ParentAreaId, Kind = src.Kind,
                Name = name, Code = null, SortOrder = src.SortOrder + 1, Quantity = src.Quantity, Unit = src.Unit,
            };
            db.Areas.Add(root);
            await db.SaveChangesAsync();   // assigns root id; also commits the If-Match version guard
            map[src.Id] = root.Id;

            // Level-by-level so each parent's new id is known before its children are added.
            var frontier = new List<int> { src.Id };
            while (frontier.Count > 0)
            {
                var made = new List<(int OldId, Area Clone)>();
                foreach (var oldParent in frontier)
                    if (childrenByParent.TryGetValue(oldParent, out var kids))
                        foreach (var kid in kids.OrderBy(k => k.SortOrder))
                        {
                            var clone = new Area
                            {
                                ProjectId = kid.ProjectId, ParentAreaId = map[oldParent], Kind = kid.Kind,
                                Name = kid.Name, Code = kid.Code, SortOrder = kid.SortOrder, Quantity = kid.Quantity, Unit = kid.Unit,
                            };
                            db.Areas.Add(clone);
                            made.Add((kid.Id, clone));
                        }
                if (made.Count == 0) break;
                await db.SaveChangesAsync();
                foreach (var (oldId, clone) in made) map[oldId] = clone.Id;
                frontier = made.Select(m => m.OldId).ToList();
            }

            // Duplicate this estimate's activities tagged to any area in the cloned subtree.
            var oldIds = map.Keys.Select(k => (int?)k).ToList();
            var items = await db.BoqItems.Include(x => x.CostComponents)
                .Where(x => x.Section.EstimateId == id && oldIds.Contains(x.AreaId)).ToListAsync();
            foreach (var it in items)
                db.BoqItems.Add(new BoqItem
                {
                    SectionId = it.SectionId, ItemCode = it.ItemCode, Description = it.Description, Unit = it.Unit,
                    Quantity = it.Quantity, AssemblyId = it.AssemblyId, UnitRate = it.UnitRate, Kind = it.Kind,
                    AreaId = map[it.AreaId!.Value], SortOrder = it.SortOrder,
                    CostComponents = it.CostComponents
                        .Select(c => new ItemCostComponent { CostComponentTypeId = c.CostComponentTypeId, Value = c.Value, Quantity = c.Quantity, Rate = c.Rate }).ToList(),
                });
            await db.SaveChangesAsync();
            var bd = await calc.RecomputeAsync(id);
            await tx.CommitAsync();
            return bd;
            });
            return Results.Ok(result);
        });

        // ── Preliminaries (module: prelims-markups) ──────────────────────────
        grp.MapPost("/{id:int}/preliminaries", async (int id, PrelimInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Prelims, ModuleAction.Add, access, perm); if (g is not null) return g;
            if (!Enum.TryParse<PreliminaryKind>(i.Kind, true, out var kind)) return Bad($"Invalid kind '{i.Kind}'");
            db.Preliminaries.Add(new Preliminary { EstimateId = id, Description = i.Description.Trim(), Kind = kind, Amount = i.Amount, SortOrder = i.SortOrder });
            await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        grp.MapPut("/{id:int}/preliminaries/{pid:int}", async (int id, int pid, PrelimInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Prelims, ModuleAction.Edit, access, perm); if (g is not null) return g;
            var p = await db.Preliminaries.FirstOrDefaultAsync(x => x.Id == pid && x.EstimateId == id); if (p is null) return NotFound();
            if (!Enum.TryParse<PreliminaryKind>(i.Kind, true, out var kind)) return Bad($"Invalid kind '{i.Kind}'");
            p.Description = i.Description.Trim(); p.Kind = kind; p.Amount = i.Amount; p.SortOrder = i.SortOrder;
            await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        grp.MapDelete("/{id:int}/preliminaries/{pid:int}", async (int id, int pid, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Prelims, ModuleAction.Delete, access, perm); if (g is not null) return g;
            var p = await db.Preliminaries.FirstOrDefaultAsync(x => x.Id == pid && x.EstimateId == id); if (p is null) return NotFound();
            db.Preliminaries.Remove(p); await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        // ── Markups (module: prelims-markups) ────────────────────────────────
        grp.MapPost("/{id:int}/markups", async (int id, MarkupInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Prelims, ModuleAction.Add, access, perm); if (g is not null) return g;
            if (!Enum.TryParse<MarkupType>(i.Type, true, out var type)) return Bad($"Invalid markup type '{i.Type}'");
            if (i.Percentage < 0) return Bad("Percentage cannot be negative");
            db.Markups.Add(new Markup { EstimateId = id, Type = type, Label = i.Label, Percentage = i.Percentage, ApplyOrder = i.ApplyOrder });
            await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        grp.MapPut("/{id:int}/markups/{mid:int}", async (int id, int mid, MarkupInput i, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Prelims, ModuleAction.Edit, access, perm); if (g is not null) return g;
            var m = await db.Markups.FirstOrDefaultAsync(x => x.Id == mid && x.EstimateId == id); if (m is null) return NotFound();
            if (!Enum.TryParse<MarkupType>(i.Type, true, out var type)) return Bad($"Invalid markup type '{i.Type}'");
            if (i.Percentage < 0) return Bad("Percentage cannot be negative");
            m.Type = type; m.Label = i.Label; m.Percentage = i.Percentage; m.ApplyOrder = i.ApplyOrder;
            await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });

        grp.MapDelete("/{id:int}/markups/{mid:int}", async (int id, int mid, ClaimsPrincipal me, ProjectAccessService access, PermissionService perm, AppDbContext db, EstimateCalculator calc) =>
        {
            var g = await Guard(me, id, Prelims, ModuleAction.Delete, access, perm); if (g is not null) return g;
            var m = await db.Markups.FirstOrDefaultAsync(x => x.Id == mid && x.EstimateId == id); if (m is null) return NotFound();
            db.Markups.Remove(m); await db.SaveChangesAsync();
            return Results.Ok(await calc.RecomputeAsync(id));
        });
    }

    /// <summary>Project access (hides existence with 404) then module permission (403).</summary>
    private static async Task<IResult?> Guard(
        ClaimsPrincipal me, int estimateId, string module, ModuleAction action,
        ProjectAccessService access, PermissionService perm)
    {
        if (!await access.CanAccessEstimateAsync(me, estimateId))
            return Results.NotFound(new { error = "Estimate not found or not accessible" });
        if (!await perm.CanAsync(me, module, action))
            return Results.Json(new { error = $"Missing '{action}' permission on {module}" }, statusCode: 403);
        return null;
    }

    /// <summary>Deep-copy an estimate (BOQ tree + preliminaries + markups) into a
    /// target project as the next revision (Draft). Used by same-project Duplicate
    /// and cross-project Copy. Sections are recreated first so nested ParentSectionId
    /// can be remapped via an old→new id map; then items, prelims and markups.</summary>
    private static async Task<Estimate> DeepCopyEstimateAsync(AppDbContext db, EstimateCalculator calc, Estimate src, int targetProjectId, string? title)
    {
        // All-or-nothing: the clone spans many SaveChanges calls (estimate, then each
        // section, then items/prelims/markups, then recompute). One transaction means a
        // failure partway can never leave an orphaned half-copied Draft behind. Run through
        // the execution strategy so the whole unit retries together on a transient fault
        // (required because the DbContext has retry-on-failure enabled).
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
        await using var tx = await db.Database.BeginTransactionAsync();

        var nextRev = ((await db.Estimates.Where(e => e.ProjectId == targetProjectId).MaxAsync(e => (int?)e.Revision)) ?? 0) + 1;
        var clone = new Estimate
        {
            ProjectId = targetProjectId,
            Revision  = nextRev,
            Title     = string.IsNullOrWhiteSpace(title) ? $"{src.Title} (rev {nextRev})" : title!.Trim(),
            Status    = EstimateStatus.Draft,
            Currency  = src.Currency,
            DefaultLaborRate = src.DefaultLaborRate,
            TaxRatePct = src.TaxRatePct,
        };
        db.Estimates.Add(clone);
        await db.SaveChangesAsync();

        var sectionMap = new Dictionary<int, int>();
        foreach (var s in src.Sections.OrderBy(x => x.SortOrder))
        {
            var ns = new BoqSection { EstimateId = clone.Id, Code = s.Code, Title = s.Title, SortOrder = s.SortOrder };
            db.BoqSections.Add(ns);
            await db.SaveChangesAsync();
            sectionMap[s.Id] = ns.Id;
            foreach (var it in s.Items.OrderBy(x => x.SortOrder))
                db.BoqItems.Add(new BoqItem
                {
                    SectionId = ns.Id, ItemCode = it.ItemCode, Description = it.Description, Unit = it.Unit,
                    Quantity = it.Quantity, SortOrder = it.SortOrder, AssemblyId = it.AssemblyId, UnitRate = it.UnitRate, Kind = it.Kind,
                    // Areas are project-scoped: keep the tag for a same-project duplicate, drop it on cross-project copy.
                    AreaId = targetProjectId == src.ProjectId ? it.AreaId : null,
                    CostComponents = it.CostComponents
                        .Select(c => new ItemCostComponent { CostComponentTypeId = c.CostComponentTypeId, Value = c.Value, Quantity = c.Quantity, Rate = c.Rate }).ToList(),
                });
        }
        foreach (var s in src.Sections.Where(x => x.ParentSectionId != null))
            if (sectionMap.TryGetValue(s.Id, out var newId) && sectionMap.TryGetValue(s.ParentSectionId!.Value, out var newParent))
            {
                var ns = await db.BoqSections.FirstOrDefaultAsync(x => x.Id == newId);
                if (ns is not null) ns.ParentSectionId = newParent;
            }

        foreach (var p in src.Preliminaries.OrderBy(x => x.SortOrder))
            db.Preliminaries.Add(new Preliminary { EstimateId = clone.Id, Description = p.Description, Kind = p.Kind, Amount = p.Amount, SortOrder = p.SortOrder });
        foreach (var m in src.Markups.OrderBy(x => x.ApplyOrder))
            db.Markups.Add(new Markup { EstimateId = clone.Id, Type = m.Type, Label = m.Label, Percentage = m.Percentage, ApplyOrder = m.ApplyOrder });

        await db.SaveChangesAsync();
        await calc.RecomputeAsync(clone.Id);
        await tx.CommitAsync();
        return clone;
        });
    }

    private static EstimateSummaryDto Summ(Estimate e) =>
        new(e.Id, e.Revision, e.Title, e.Status.ToString(), e.Currency, e.BidPrice, e.UpdatedAt);

    private static IResult Bad(string msg) => Results.BadRequest(new { error = msg });
    private static IResult NotFound() => Results.NotFound(new { error = "Not found" });

    /// <summary>Parse a BOQ line kind (case-insensitive). Null/empty → Normal (the
    /// default, so existing clients that omit the field are unaffected).</summary>
    private static bool ParseKind(string? s, out BoqItemKind kind)
    {
        if (string.IsNullOrWhiteSpace(s)) { kind = BoqItemKind.Normal; return true; }
        return Enum.TryParse(s, true, out kind);
    }

    /// <summary>Endpoint metadata marker: this estimate write stays available even when the
    /// revision is Published/Superseded. The status-lock filter is fail-closed, so only routes
    /// tagged with <see cref="AllowWhenFinalised"/> escape the lock (status/title PUT, recompute,
    /// what-if). Everything else under /api/estimates is locked by default.</summary>
    private sealed class AllowWhenFinalisedMarker { }
    private static readonly AllowWhenFinalisedMarker AllowFinalised = new();
    private static RouteHandlerBuilder AllowWhenFinalised(this RouteHandlerBuilder b) => b.WithMetadata(AllowFinalised);

    /// <summary>Areas are project-scoped — a BOQ item may only be tagged with an area of
    /// its own estimate's project, else the area roll-up silently mis-buckets it as
    /// Unassigned. Returns null when there's no area (nothing to check) or it's valid;
    /// a 400 otherwise.</summary>
    private static async Task<IResult?> ValidateAreaAsync(AppDbContext db, int estimateId, int? areaId)
    {
        if (areaId is null) return null;
        var areaProjectId = await db.Estimates.Where(e => e.Id == estimateId).Select(e => e.ProjectId).FirstAsync();
        if (!await db.Areas.AnyAsync(a => a.Id == areaId && a.ProjectId == areaProjectId)) return Bad("Area not found");
        return null;
    }

    /// <summary>Validate a BOQ item's cost-component lines: no duplicate types and
    /// every referenced type exists in this tenant's catalog. Returns null when OK.</summary>
    private static async Task<IResult?> ValidateComponents(AppDbContext db, List<ItemCostComponentInput>? comps)
    {
        if (comps is null || comps.Count == 0) return null;
        var ids = comps.Select(c => c.TypeId).ToList();
        if (ids.Distinct().Count() != ids.Count) return Bad("Duplicate cost-component type on the item.");
        var known = await db.CostComponentTypes.Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync();
        if (known.Count != ids.Count) return Bad("Unknown cost-component type.");
        if (comps.Any(c => c.Quantity is < 0 || c.Rate is < 0)) return Bad("Quantity and rate cannot be negative.");
        return null;
    }

    /// <summary>Materialise cost-component inputs into entities, resolving each line's
    /// canonical <c>Value</c>. For an Amount-kind type with a quantity × rate breakdown,
    /// Value = Quantity × Rate (and both are stored); otherwise the supplied Value is used
    /// (a directly typed amount, or a Percent-kind percentage). Assumes the inputs already
    /// passed <see cref="ValidateComponents"/>.</summary>
    private static async Task<List<ItemCostComponent>> BuildComponentsAsync(AppDbContext db, List<ItemCostComponentInput> comps)
    {
        var ids = comps.Select(c => c.TypeId).ToList();
        var kinds = await db.CostComponentTypes.Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.CalcKind);
        var result = new List<ItemCostComponent>(comps.Count);
        foreach (var c in comps)
        {
            var isAmount = kinds.TryGetValue(c.TypeId, out var k) && k == CostCalcKind.Amount;
            var useQtyRate = isAmount && (c.Quantity is not null || c.Rate is not null);
            result.Add(new ItemCostComponent
            {
                CostComponentTypeId = c.TypeId,
                Quantity = useQtyRate ? c.Quantity ?? 0m : null,
                Rate     = useQtyRate ? c.Rate ?? 0m : null,
                Value    = useQtyRate ? (c.Quantity ?? 0m) * (c.Rate ?? 0m) : c.Value,
            });
        }
        return result;
    }
}
