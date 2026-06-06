using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;

namespace BidBuilder.Api.Endpoints;

/// <summary>One match in global search: where to go + how to label it.</summary>
public record SearchHit(string Title, string? Subtitle, string Link);
/// <summary>A typed cluster of hits (Projects / Estimates / Resources / Assemblies).</summary>
public record SearchGroup(string Type, string Label, IReadOnlyList<SearchHit> Hits);
/// <summary>The full result set for a query (only non-empty groups are returned).</summary>
public record SearchResults(string Query, IReadOnlyList<SearchGroup> Groups);

/// <summary>
/// 20.4 — Cross-project quick search. One call sweeps projects, estimates, the
/// resource library and assemblies. Results respect the scoping layer:
/// project + estimate hits are restricted to projects the caller can access
/// (admins see all), and the resource / assembly groups appear only when the
/// caller holds the relevant module View permission. Tenant isolation is handled
/// by the global query filter, so cross-tenant rows can never surface.
/// </summary>
public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search", async (string? q, int? take, ClaimsPrincipal me,
            AppDbContext db, ProjectAccessService access, PermissionService perm) =>
        {
            var term = (q ?? "").Trim();
            // Need at least 2 chars — a 1-char ILike would scan the world for noise.
            if (term.Length < 2) return Results.Ok(new SearchResults(term, Array.Empty<SearchGroup>()));

            var n = Math.Clamp(take ?? 6, 1, 20);
            var pattern = "%" + Like(term) + "%";
            var groups = new List<SearchGroup>();

            // ── Projects (scoped to accessible) ──────────────────────────────
            var accessible = access.Accessible(me);
            var projects = await accessible
                .Where(p => EF.Functions.ILike(p.Code, pattern)
                         || EF.Functions.ILike(p.Name, pattern)
                         || (p.ClientName != null && EF.Functions.ILike(p.ClientName, pattern))
                         || (p.Location != null && EF.Functions.ILike(p.Location, pattern)))
                .OrderBy(p => p.Code)
                .Take(n)
                .Select(p => new { p.Id, p.Code, p.Name, p.ClientName })
                .ToListAsync();
            if (projects.Count > 0)
                groups.Add(new SearchGroup("project", "Projects", projects
                    .Select(p => new SearchHit(p.Name,
                        p.ClientName is { Length: > 0 } ? $"{p.Code} · {p.ClientName}" : p.Code,
                        $"/projects/{p.Id}"))
                    .ToList()));

            // ── Estimates (title), restricted to accessible projects ─────────
            var accessibleIds = accessible.Select(p => p.Id);
            var estimates = await db.Estimates
                .Where(e => accessibleIds.Contains(e.ProjectId) && EF.Functions.ILike(e.Title, pattern))
                .OrderByDescending(e => e.UpdatedAt)
                .Take(n)
                .Select(e => new { e.Id, e.ProjectId, e.Title, e.Revision, e.Status })
                .ToListAsync();
            if (estimates.Count > 0)
                groups.Add(new SearchGroup("estimate", "Estimates", estimates
                    .Select(e => new SearchHit(e.Title, $"Rev {e.Revision} · {e.Status}", $"/projects/{e.ProjectId}"))
                    .ToList()));

            // ── Resource library (code/name), gated by module View ───────────
            if (await perm.CanAsync(me, "resource-library", ModuleAction.View))
            {
                var hits = new List<SearchHit>();
                async Task AddResAsync<T>(IQueryable<T> set, string kind, Func<T, (string Code, string Name)> get) where T : class
                {
                    var rows = await set.Take(n).ToListAsync();
                    foreach (var r in rows) { var (code, name) = get(r); hits.Add(new SearchHit(name, $"{kind} · {code}", "/resources")); }
                }
                await AddResAsync(db.LaborResources.Where(r => EF.Functions.ILike(r.Code, pattern) || EF.Functions.ILike(r.Name, pattern)), "Labour", r => (r.Code, r.Name));
                await AddResAsync(db.MaterialResources.Where(r => EF.Functions.ILike(r.Code, pattern) || EF.Functions.ILike(r.Name, pattern)), "Material", r => (r.Code, r.Name));
                await AddResAsync(db.EquipmentResources.Where(r => EF.Functions.ILike(r.Code, pattern) || EF.Functions.ILike(r.Name, pattern)), "Equipment", r => (r.Code, r.Name));
                await AddResAsync(db.Subcontractors.Where(r => EF.Functions.ILike(r.Code, pattern) || EF.Functions.ILike(r.Name, pattern)), "Subcontractor", r => (r.Code, r.Name));
                if (hits.Count > 0)
                    groups.Add(new SearchGroup("resource", "Resources", hits.Take(n).ToList()));
            }

            // ── Assemblies (code/name), gated by module View ─────────────────
            if (await perm.CanAsync(me, "assemblies", ModuleAction.View))
            {
                var asm = await db.Assemblies
                    .Where(a => EF.Functions.ILike(a.Code, pattern) || EF.Functions.ILike(a.Name, pattern))
                    .OrderBy(a => a.Code)
                    .Take(n)
                    .Select(a => new { a.Id, a.Code, a.Name })
                    .ToListAsync();
                if (asm.Count > 0)
                    groups.Add(new SearchGroup("assembly", "Assemblies", asm
                        .Select(a => new SearchHit(a.Name, a.Code, $"/assemblies/{a.Id}"))
                        .ToList()));
            }

            return Results.Ok(new SearchResults(term, groups));
        }).RequireAuthorization();
    }

    /// <summary>Escape ILike wildcards so a user typing "%" or "_" searches literally
    /// (default PostgreSQL ILike escape char is backslash).</summary>
    private static string Like(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
