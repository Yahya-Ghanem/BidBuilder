using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Endpoints;

/// <summary>The signed-in user's sidebar personalisation: pinned (favourited)
/// projects and the most-recently-visited list (most-recent first).</summary>
public record UserPreferencesDto(int[] PinnedProjectIds, int[] RecentProjectIds);

/// <summary>Pin or unpin a project for the caller.</summary>
public record PinProjectInput(int ProjectId, bool Pinned);

/// <summary>Record that the caller just visited a project (moves it to the front
/// of the recent list).</summary>
public record RecordRecentInput(int ProjectId);

/// <summary>
/// 27.5 — Per-user project favourites + recents (<c>/api/preferences</c>). Personal
/// state, NOT RBAC-gated — any signed-in user manages their own pins/recents. A user
/// may only pin / record projects they can actually access (project-team scoped), so a
/// pin can't be used to probe for the existence of a project in another team.
/// </summary>
public static class UserPreferenceEndpoints
{
    public static void MapUserPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/preferences").RequireAuthorization();

        // The caller's own preferences (absence of a row == empty lists).
        grp.MapGet("/", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var pref = await Load(db, me.Id());
            return Results.Ok(ToDto(pref));
        });

        // Pin / unpin a project. 404 if the caller can't access it (hides existence).
        grp.MapPut("/pinned", async (PinProjectInput input, ClaimsPrincipal me, AppDbContext db, ProjectAccessService access) =>
        {
            if (input.Pinned && !await access.CanAccessProjectAsync(me, input.ProjectId))
                return Results.NotFound(new { error = "Project not found or not accessible" });

            var pref = await LoadOrCreate(db, me.Id());
            var ids = Parse(pref.PinnedProjectIds);
            if (input.Pinned) { if (!ids.Contains(input.ProjectId)) ids.Add(input.ProjectId); }
            else ids.Remove(input.ProjectId);

            pref.PinnedProjectIds = Serialize(ids);
            pref.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(pref));
        });

        // Record a project visit (move it to the front of the recent list, capped).
        grp.MapPost("/recent", async (RecordRecentInput input, ClaimsPrincipal me, AppDbContext db, ProjectAccessService access) =>
        {
            if (!await access.CanAccessProjectAsync(me, input.ProjectId))
                return Results.NotFound(new { error = "Project not found or not accessible" });

            var pref = await LoadOrCreate(db, me.Id());
            var ids = Parse(pref.RecentProjectIds);
            ids.Remove(input.ProjectId);          // de-dupe: drop any earlier occurrence
            ids.Insert(0, input.ProjectId);       // most-recent first
            if (ids.Count > UserPreferences.RecentCap) ids = ids.GetRange(0, UserPreferences.RecentCap);

            pref.RecentProjectIds = Serialize(ids);
            pref.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(pref));
        });
    }

    private static Task<UserPreferences?> Load(AppDbContext db, int userId) =>
        db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId);

    private static async Task<UserPreferences> LoadOrCreate(AppDbContext db, int userId)
    {
        var pref = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        if (pref is null)
        {
            pref = new UserPreferences { UserId = userId };   // TenantId auto-stamped on save
            db.UserPreferences.Add(pref);
        }
        return pref;
    }

    private static UserPreferencesDto ToDto(UserPreferences? pref) =>
        pref is null
            ? new UserPreferencesDto([], [])
            : new UserPreferencesDto([.. Parse(pref.PinnedProjectIds)], [.. Parse(pref.RecentProjectIds)]);

    /// <summary>Parse a comma-separated id list, dropping blanks/dupes while preserving order.</summary>
    private static List<int> Parse(string csv)
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(csv)) return ids;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var id) && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    private static string Serialize(IEnumerable<int> ids) => string.Join(',', ids);
}
