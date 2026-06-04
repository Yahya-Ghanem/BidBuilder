using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;

namespace BidBuilder.Api.Endpoints;

public record AuditDto(long Id, DateTime At, string? ActorEmail, string? ActorName, string? ActorRole,
    string Action, string Entity, string? EntityKey, string? Summary);

/// <summary>One page of audit events plus the total matching the current filter.</summary>
public record AuditPageDto(long Total, int Take, int Skip, List<AuditDto> Items);

/// <summary>Read-only audit trail (tenant admin only). Newest first, filterable + paged.</summary>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/audit").RequireAuthorization();

        grp.MapGet("/", async (ClaimsPrincipal me, AppDbContext db,
            int? take, int? skip, string? action, string? entity, string? actor, string? from, string? to) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can view the audit log" }, statusCode: 403);

            var n = Math.Clamp(take ?? 50, 1, 500);
            var s = Math.Max(skip ?? 0, 0);

            var q = db.AuditEvents.AsQueryable();
            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(e => e.Action == action);
            if (!string.IsNullOrWhiteSpace(entity)) q = q.Where(e => e.Entity == entity);
            if (!string.IsNullOrWhiteSpace(actor))
            {
                var a = $"%{actor.Trim()}%";
                q = q.Where(e => (e.ActorName != null && EF.Functions.ILike(e.ActorName, a))
                              || (e.ActorEmail != null && EF.Functions.ILike(e.ActorEmail, a)));
            }
            // Dates arrive as yyyy-MM-dd; treat as whole UTC days ([from 00:00, to+1 00:00)).
            if (ParseDay(from) is { } fromUtc) q = q.Where(e => e.At >= fromUtc);
            if (ParseDay(to) is { } toDay) { var toExcl = toDay.AddDays(1); q = q.Where(e => e.At < toExcl); }

            var total = await q.LongCountAsync();
            var items = await q
                .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
                .Skip(s).Take(n)
                .Select(e => new AuditDto(e.Id, e.At, e.ActorEmail, e.ActorName, e.ActorRole, e.Action, e.Entity, e.EntityKey, e.Summary))
                .ToListAsync();
            return Results.Ok(new AuditPageDto(total, n, s, items));
        });

        // Distinct action codes present in this tenant's trail — populates the filter dropdown.
        grp.MapGet("/actions", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can view the audit log" }, statusCode: 403);
            var actions = await db.AuditEvents.Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync();
            return Results.Ok(actions);
        });
    }

    private static DateTime? ParseDay(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;
}
