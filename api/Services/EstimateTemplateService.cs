using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

// ── Serialized template payload (the shape stored in EstimateTemplate.PayloadJson) ──
public record TemplatePayload(
    string Currency, decimal DefaultLaborRate, decimal? TaxRatePct,
    List<TplSection> Sections, List<TplPrelim> Preliminaries, List<TplMarkup> Markups, List<TplRisk> Risks);

/// <summary>TempId/ParentTempId carry the source section ids so nesting survives the round-trip
/// (remapped to fresh ids on apply, exactly like the deep-copy clone).</summary>
public record TplSection(int TempId, int? ParentTempId, string Code, string Title, int SortOrder, List<TplItem> Items);
public record TplItem(string ItemCode, string Description, string? Unit, decimal Quantity, int SortOrder,
    int? AssemblyId, decimal UnitRate, string Kind, List<TplComp> Components);
public record TplComp(int CostComponentTypeId, decimal Value, decimal? Quantity, decimal? Rate);
public record TplPrelim(string Description, string Kind, decimal Amount, int SortOrder);
public record TplMarkup(string? Type, string? Label, decimal Percentage, int ApplyOrder);
public record TplRisk(string Title, string? Category, decimal ProbabilityPct, decimal ImpactAmount, string? Note, int SortOrder);

/// <summary>
/// 21.3 — Serializes an estimate's structure to a reusable template and rebuilds a new
/// estimate from one. The apply path mirrors the deep-copy clone (sections-first with a
/// parent remap, then items/components/prelims/markups/risks, then recompute) but reads
/// from the JSON payload instead of a live source. Tenant-scoped FK references (assembly,
/// cost-component type) are re-validated against the TARGET tenant on apply and degrade
/// gracefully — a deleted assembly falls back to the stored unit rate; a deleted cost type
/// drops just that build-up line. Area tags are never carried (areas are project-scoped).
/// </summary>
public class EstimateTemplateService(AppDbContext db, EstimateCalculator calc)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(TemplatePayload payload) => JsonSerializer.Serialize(payload, Json);

    /// <summary>Build a template payload from a fully-loaded estimate graph
    /// (Sections→Items→CostComponents, Preliminaries, Markups, Risks).</summary>
    public TemplatePayload Build(Estimate src) => new(
        src.Currency, src.DefaultLaborRate, src.TaxRatePct,
        src.Sections.OrderBy(s => s.SortOrder).Select(s => new TplSection(
            s.Id, s.ParentSectionId, s.Code, s.Title, s.SortOrder,
            s.Items.OrderBy(i => i.SortOrder).Select(i => new TplItem(
                i.ItemCode, i.Description, i.Unit, i.Quantity, i.SortOrder,
                i.AssemblyId, i.UnitRate, i.Kind.ToString(),
                i.CostComponents.Select(c => new TplComp(c.CostComponentTypeId, c.Value, c.Quantity, c.Rate)).ToList()
            )).ToList()
        )).ToList(),
        src.Preliminaries.OrderBy(p => p.SortOrder).Select(p => new TplPrelim(p.Description, p.Kind.ToString(), p.Amount, p.SortOrder)).ToList(),
        src.Markups.OrderBy(m => m.ApplyOrder).Select(m => new TplMarkup(m.Type.ToString(), m.Label, m.Percentage, m.ApplyOrder)).ToList(),
        src.Risks.OrderBy(r => r.SortOrder).Select(r => new TplRisk(r.Title, r.Category.ToString(), r.ProbabilityPct, r.ImpactAmount, r.Note, r.SortOrder)).ToList());

    public static (int Sections, int Items) Counts(TemplatePayload p) =>
        (p.Sections.Count, p.Sections.Sum(s => s.Items.Count));

    /// <summary>24.3 — Re-derive the section/item counts from a payload JSON string,
    /// used by the cross-tenant import endpoint where we deliberately do NOT trust
    /// any counts the uploaded envelope claims. Returns (0, 0) when the payload is
    /// missing/malformed; the caller treats that as a valid but empty template.</summary>
    public static (int Sections, int Items) CountsFromJson(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return (0, 0);
        try
        {
            var p = JsonSerializer.Deserialize<TemplatePayload>(payloadJson, Json);
            return p is null ? (0, 0) : Counts(p);
        }
        catch (JsonException) { return (0, 0); }
    }

    /// <summary>Create a new Draft estimate in <paramref name="targetProjectId"/> from a template.
    /// Returns the recomputed estimate. Runs in one transaction through the execution strategy
    /// (retry-safe), just like the clone.</summary>
    public async Task<Estimate> ApplyAsync(int targetProjectId, string? title, EstimateTemplate template)
    {
        var payload = JsonSerializer.Deserialize<TemplatePayload>(template.PayloadJson, Json)
                      ?? throw new InvalidOperationException("Template payload is corrupt.");

        // Validate tenant-scoped references against the target tenant (query filter applies).
        var asmIds = payload.Sections.SelectMany(s => s.Items)
            .Where(i => i.AssemblyId.HasValue).Select(i => i.AssemblyId!.Value).Distinct().ToList();
        var validAsm = asmIds.Count == 0 ? new HashSet<int>()
            : (await db.Assemblies.Where(a => asmIds.Contains(a.Id)).Select(a => a.Id).ToListAsync()).ToHashSet();

        var typeIds = payload.Sections.SelectMany(s => s.Items).SelectMany(i => i.Components)
            .Select(c => c.CostComponentTypeId).Distinct().ToList();
        var validTypes = typeIds.Count == 0 ? new HashSet<int>()
            : (await db.CostComponentTypes.Where(t => typeIds.Contains(t.Id)).Select(t => t.Id).ToListAsync()).ToHashSet();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            var nextRev = ((await db.Estimates.Where(e => e.ProjectId == targetProjectId).MaxAsync(e => (int?)e.Revision)) ?? 0) + 1;
            var est = new Estimate
            {
                ProjectId = targetProjectId,
                Revision  = nextRev,
                Title     = string.IsNullOrWhiteSpace(title) ? $"{template.Name} (rev {nextRev})" : title!.Trim(),
                Status    = EstimateStatus.Draft,
                Currency  = payload.Currency,
                DefaultLaborRate = payload.DefaultLaborRate,
                TaxRatePct = payload.TaxRatePct,
            };
            db.Estimates.Add(est);
            await db.SaveChangesAsync();

            var sectionMap = new Dictionary<int, int>();
            foreach (var s in payload.Sections.OrderBy(x => x.SortOrder))
            {
                var ns = new BoqSection { EstimateId = est.Id, Code = s.Code, Title = s.Title, SortOrder = s.SortOrder };
                db.BoqSections.Add(ns);
                await db.SaveChangesAsync();
                sectionMap[s.TempId] = ns.Id;
                foreach (var it in s.Items.OrderBy(x => x.SortOrder))
                    db.BoqItems.Add(new BoqItem
                    {
                        SectionId = ns.Id, ItemCode = it.ItemCode, Description = it.Description, Unit = it.Unit,
                        Quantity = it.Quantity, SortOrder = it.SortOrder, UnitRate = it.UnitRate,
                        Kind = Enum.TryParse<BoqItemKind>(it.Kind, true, out var k) ? k : BoqItemKind.Normal,
                        // Drop a dangling assembly ref (falls back to the stored unit rate); never carry areas.
                        AssemblyId = it.AssemblyId is { } aid && validAsm.Contains(aid) ? aid : null,
                        AreaId = null,
                        CostComponents = it.Components
                            .Where(c => validTypes.Contains(c.CostComponentTypeId))
                            .Select(c => new ItemCostComponent { CostComponentTypeId = c.CostComponentTypeId, Value = c.Value, Quantity = c.Quantity, Rate = c.Rate })
                            .ToList(),
                    });
            }
            // Second pass: remap nested section parents now that every new id is known.
            foreach (var s in payload.Sections.Where(x => x.ParentTempId is not null))
                if (sectionMap.TryGetValue(s.TempId, out var newId) && sectionMap.TryGetValue(s.ParentTempId!.Value, out var newParent))
                {
                    var ns = await db.BoqSections.FirstOrDefaultAsync(x => x.Id == newId);
                    if (ns is not null) ns.ParentSectionId = newParent;
                }

            foreach (var p in payload.Preliminaries.OrderBy(x => x.SortOrder))
                db.Preliminaries.Add(new Preliminary
                {
                    EstimateId = est.Id, Description = p.Description, Amount = p.Amount, SortOrder = p.SortOrder,
                    Kind = Enum.TryParse<PreliminaryKind>(p.Kind, true, out var pk) ? pk : PreliminaryKind.Fixed,
                });
            foreach (var m in payload.Markups.OrderBy(x => x.ApplyOrder))
                db.Markups.Add(new Markup
                {
                    EstimateId = est.Id, Label = m.Label, Percentage = m.Percentage, ApplyOrder = m.ApplyOrder,
                    Type = Enum.TryParse<MarkupType>(m.Type, true, out var mt) ? mt : default,
                });
            foreach (var r in payload.Risks.OrderBy(x => x.SortOrder))
                db.RiskItems.Add(new RiskItem
                {
                    EstimateId = est.Id, Title = r.Title, ProbabilityPct = r.ProbabilityPct,
                    ImpactAmount = r.ImpactAmount, Note = r.Note, SortOrder = r.SortOrder,
                    Category = Enum.TryParse<RiskCategory>(r.Category, true, out var rc) ? rc : default,
                });

            await db.SaveChangesAsync();
            await calc.RecomputeAsync(est.Id);
            await tx.CommitAsync();
            return est;
        });
    }
}
