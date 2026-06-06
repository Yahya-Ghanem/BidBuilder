using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>
/// One slice of the bid register — a single project's outcome figures plus the
/// computed bid/award variance. Stays light: aggregations live in the summary DTO.
/// </summary>
public record BidRegisterRow(
    int    ProjectId,
    string Code,
    string Name,
    string? ClientName,
    string Currency,
    int?   ProjectTypeId,
    string? ProjectTypeName,
    string Status,                          // ProjectStatus.ToString()
    DateTime? TenderDueAt,
    DateTime? DecisionAt,
    decimal? SubmittedBidValue,
    decimal? AwardedValue,
    decimal? FinalCost,
    /// <summary>(AwardedValue − SubmittedBidValue) / SubmittedBidValue × 100, null when
    /// either side is missing. Positive = awarded above bid (revised up after submission).</summary>
    decimal? BidVsAwardPct,
    string? WinLossNote);

/// <summary>Per-bucket roll-up. A bucket can be a project-type, a client name,
/// or a calendar period — the dimension is tagged in <see cref="Dimension"/>.</summary>
public record BidAnalyticsBucket(
    string Dimension,                       // "project-type" | "client" | "period"
    string Key,                             // human label (e.g. "Civil", "Acme Corp", "2026-Q1")
    int    Total,                           // projects decided (Won + Lost) in this bucket
    int    Won,
    int    Lost,
    /// <summary>Won / (Won + Lost) × 100, 2dp. 0 when Total == 0.</summary>
    decimal HitRatePct,
    /// <summary>Weighted-average BidVsAwardPct over rows where BOTH SubmittedBidValue
    /// and AwardedValue are present. Null when no row qualifies.</summary>
    decimal? AvgBidVsAwardPct,
    /// <summary>Sum of AwardedValue for Won rows (in project currency — pre-aggregation
    /// FX normalization is out of scope for v1; the UI flags mixed-currency buckets).</summary>
    decimal AwardedValueSum,
    /// <summary>True when the rows in this bucket span more than one currency, so
    /// AwardedValueSum is a "sum of apples + oranges" the UI should label cautiously.</summary>
    bool   MixedCurrency);

public record BidAnalyticsResult(
    /// <summary>Tenant-wide rollup over the date-window selection.</summary>
    BidAnalyticsBucket Overall,
    List<BidAnalyticsBucket> ByProjectType,
    List<BidAnalyticsBucket> ByClient,
    List<BidAnalyticsBucket> ByPeriod,
    List<BidRegisterRow>     Register);

/// <summary>Partial update of a project's bid-outcome fields. Every field is
/// optional; null = leave alone. Pass <see cref="ClearWinLossNote"/> to wipe the
/// note (since null can't distinguish "absent" from "clear" in JSON).</summary>
public record BidOutcomeRequest(
    decimal? SubmittedBidValue,
    decimal? AwardedValue,
    decimal? FinalCost,
    DateTime? DecisionAt,
    string?  WinLossNote,
    bool?    ClearWinLossNote);

/// <summary>
/// Read-only bid analytics + the bid-outcome write endpoint (19.1). Read paths are
/// scoped through <see cref="ProjectAccessService"/> so a user only sees the bid
/// data for projects they're already allowed into. Write path requires the
/// <c>projects</c> module Edit permission.
/// </summary>
public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/analytics").RequireAuthorization();

        // GET /api/analytics/bids — bid register + roll-ups.
        //   ?from=YYYY-MM-DD  &to=YYYY-MM-DD  filter by DecisionAt window (inclusive)
        // No DecisionAt → still counted in the Register list, but excluded from any
        // hit-rate / period bucket (we can't classify a project that hasn't been decided).
        grp.MapGet("/bids", async (ClaimsPrincipal me, ProjectAccessService access, AppDbContext db,
            DateTime? from, DateTime? to) =>
        {
            // Scope to the projects the caller may see (admins: all in tenant; others:
            // only those their teams are assigned to). Same gate as the projects list.
            var query = access.Accessible(me);

            // Pre-load: keep enough columns for the per-bucket aggregations + the register.
            var rows = await query
                .Select(p => new {
                    p.Id, p.Code, p.Name, p.ClientName, p.Currency, p.Status,
                    p.ProjectTypeId, ProjectTypeName = p.ProjectTypeId != null
                        ? db.ProjectTypes.Where(t => t.Id == p.ProjectTypeId).Select(t => t.Name).FirstOrDefault()
                        : null,
                    p.TenderDueAt, p.DecisionAt, p.SubmittedBidValue, p.AwardedValue, p.FinalCost, p.WinLossNote,
                })
                .ToListAsync();

            // Window filter (decision-based). Rows outside the window are dropped for the
            // bucket aggregations; the register list keeps EVERY accessible row regardless,
            // so the user can find one they're recording outcomes on.
            bool InWindow(DateTime? d) =>
                d is { } v && (from is null || v >= from) && (to is null || v <= to.Value.AddDays(1).AddTicks(-1));

            static decimal? BidVsAward(decimal? bid, decimal? award) =>
                bid is { } b && b > 0m && award is { } a
                    ? Math.Round((a - b) / b * 100m, 2, MidpointRounding.AwayFromZero)
                    : null;

            var register = rows
                .OrderByDescending(r => r.DecisionAt ?? r.TenderDueAt ?? DateTime.MinValue)
                .Select(r => new BidRegisterRow(
                    r.Id, r.Code, r.Name, r.ClientName, r.Currency,
                    r.ProjectTypeId, r.ProjectTypeName,
                    r.Status.ToString(),
                    r.TenderDueAt, r.DecisionAt,
                    r.SubmittedBidValue, r.AwardedValue, r.FinalCost,
                    BidVsAward(r.SubmittedBidValue, r.AwardedValue),
                    r.WinLossNote))
                .ToList();

            // Bucket the decided rows. Won/Lost is enough — Draft/Bidding/Submitted/Archived
            // don't count toward a hit-rate. Tie-breaking: a row decided exactly at midnight
            // on the boundary day is in the window.
            var decided = rows.Where(r =>
                (r.Status == ProjectStatus.Won || r.Status == ProjectStatus.Lost) &&
                InWindow(r.DecisionAt)).ToList();

            BidAnalyticsBucket BucketOf(string dim, string key, IReadOnlyList<dynamic> group)
            {
                var total = group.Count;
                var won   = group.Count(g => g.Status == ProjectStatus.Won);
                var lost  = group.Count(g => g.Status == ProjectStatus.Lost);
                var hit   = total == 0 ? 0m : Math.Round((decimal)won / total * 100m, 2, MidpointRounding.AwayFromZero);
                var variances = group
                    .Select(g => BidVsAward((decimal?)g.SubmittedBidValue, (decimal?)g.AwardedValue))
                    .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                decimal? avgVar = variances.Count == 0 ? null : Math.Round(variances.Average(), 2, MidpointRounding.AwayFromZero);
                decimal awardSum = group.Where(g => g.Status == ProjectStatus.Won)
                    .Sum(g => (decimal?)g.AwardedValue ?? 0m);
                var currencies = group.Where(g => g.Status == ProjectStatus.Won && g.AwardedValue != null)
                    .Select(g => (string)g.Currency).Distinct().ToList();
                return new BidAnalyticsBucket(dim, key, total, won, lost, hit, avgVar, awardSum, currencies.Count > 1);
            }

            var overall = BucketOf("overall", "All", decided.Cast<dynamic>().ToList());

            var byType = decided
                .GroupBy(r => r.ProjectTypeName ?? "(uncategorised)")
                .Select(g => BucketOf("project-type", g.Key, g.Cast<dynamic>().ToList()))
                .OrderByDescending(b => b.Total).ToList();

            var byClient = decided
                .GroupBy(r => string.IsNullOrWhiteSpace(r.ClientName) ? "(no client)" : r.ClientName!)
                .Select(g => BucketOf("client", g.Key, g.Cast<dynamic>().ToList()))
                .OrderByDescending(b => b.Total).ToList();

            // Period: group by ISO year-quarter (e.g. "2026-Q2"). Quarters are the
            // estimating cadence everyone reports on; finer slices fragment the n.
            static string QuarterKey(DateTime d) => $"{d.Year:0000}-Q{((d.Month - 1) / 3) + 1}";
            var byPeriod = decided
                .GroupBy(r => QuarterKey(r.DecisionAt!.Value))
                .Select(g => BucketOf("period", g.Key, g.Cast<dynamic>().ToList()))
                .OrderBy(b => b.Key).ToList();

            return Results.Ok(new BidAnalyticsResult(overall, byType, byClient, byPeriod, register));
        })
        // .Produces<T>() makes Swashbuckle emit the 200-response shape into the OpenAPI
        // spec so the generated TS client picks it up as a typed return. Without this
        // the response is just `unknown`. Older endpoints will get the same treatment as
        // they're touched (incremental migration — drift test fails CI if the shape moves).
        .Produces<BidAnalyticsResult>(StatusCodes.Status200OK);

        // PATCH /api/projects/{id}/bid-outcome — record/update outcome figures.
        // Gated by the projects module Edit permission AND project-team access (a user
        // who can't see the project obviously can't record its outcome).
        app.MapPatch("/api/projects/{id:int}/bid-outcome",
            async (int id, BidOutcomeRequest req, ClaimsPrincipal me,
                ProjectAccessService access, PermissionService perm,
                AppDbContext db, BidBuilder.Api.Services.AuditService audit) =>
        {
            if (!await perm.CanAsync(me, "projects", ModuleAction.Edit))
                return Results.Json(new { error = "Missing 'Edit' permission on projects" }, statusCode: 403);
            if (!await access.CanAccessProjectAsync(me, id))
                return Results.NotFound(new { error = "Project not found" });
            var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound(new { error = "Project not found" });

            if (req.SubmittedBidValue.HasValue)
            {
                if (req.SubmittedBidValue.Value < 0m) return Results.Json(new { error = "SubmittedBidValue cannot be negative" }, statusCode: 400);
                p.SubmittedBidValue = req.SubmittedBidValue.Value > 0m ? req.SubmittedBidValue : null;
            }
            if (req.AwardedValue.HasValue)
            {
                if (req.AwardedValue.Value < 0m) return Results.Json(new { error = "AwardedValue cannot be negative" }, statusCode: 400);
                p.AwardedValue = req.AwardedValue.Value > 0m ? req.AwardedValue : null;
            }
            if (req.FinalCost.HasValue)
            {
                if (req.FinalCost.Value < 0m) return Results.Json(new { error = "FinalCost cannot be negative" }, statusCode: 400);
                p.FinalCost = req.FinalCost.Value > 0m ? req.FinalCost : null;
            }
            if (req.DecisionAt is { } d) p.DecisionAt = d;

            if (req.ClearWinLossNote is true) p.WinLossNote = null;
            else if (req.WinLossNote is not null) p.WinLossNote = req.WinLossNote.Trim();

            p.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "project.bid-outcome", "Project", id.ToString(),
                $"{p.Code} outcome updated (bid={p.SubmittedBidValue}, award={p.AwardedValue}, final={p.FinalCost})");
            return Results.Ok(new {
                p.Id, p.SubmittedBidValue, p.AwardedValue, p.FinalCost, p.DecisionAt, p.WinLossNote
            });
        }).RequireAuthorization();
    }
}
