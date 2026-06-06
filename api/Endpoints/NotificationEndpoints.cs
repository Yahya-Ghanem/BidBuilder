using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;

namespace BidBuilder.Api.Endpoints;

/// <summary>One notification as shown in the bell dropdown.</summary>
public record NotificationDto(long Id, string Type, string Title, string? Body, string? Link,
    string? EntityType, string? EntityKey, bool IsRead, DateTime CreatedAt);
/// <summary>A page of the current user's notifications plus their unread total.</summary>
public record NotificationListDto(int UnreadCount, IReadOnlyList<NotificationDto> Items);

/// <summary>
/// 20.3 — The signed-in user's in-app inbox. Every route is implicitly scoped to
/// <c>RecipientUserId == me.Id()</c>, so there's no cross-user read: a notification
/// belongs to exactly one recipient and only they can see or clear it. No module
/// permission gate — your own inbox is always yours.
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/notifications").RequireAuthorization();

        // GET list (newest first) + unread count. ?take caps the page (default 30);
        // ?unreadOnly=true returns just the unread ones (the count is always the
        // full unread total, regardless of the filter).
        grp.MapGet("/", async (ClaimsPrincipal me, AppDbContext db, int? take, bool? unreadOnly) =>
        {
            var uid = me.Id();
            var n = Math.Clamp(take ?? 30, 1, 100);
            var mine = db.Notifications.Where(x => x.RecipientUserId == uid);
            var unread = await mine.CountAsync(x => !x.IsRead);
            var q = (unreadOnly ?? false) ? mine.Where(x => !x.IsRead) : mine;
            var items = await q
                .OrderByDescending(x => x.Id)
                .Take(n)
                .Select(x => new NotificationDto(x.Id, x.Type, x.Title, x.Body, x.Link, x.EntityType, x.EntityKey, x.IsRead, x.CreatedAt))
                .ToListAsync();
            return Results.Ok(new NotificationListDto(unread, items));
        });

        // GET just the unread count — the lightweight endpoint the bell badge polls.
        grp.MapGet("/unread-count", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var uid = me.Id();
            var count = await db.Notifications.CountAsync(x => x.RecipientUserId == uid && !x.IsRead);
            return Results.Ok(new { count });
        });

        // POST mark one read (only your own; 404 otherwise). Idempotent.
        grp.MapPost("/{id:long}/read", async (long id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var uid = me.Id();
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.RecipientUserId == uid);
            if (n is null) return Results.NotFound(new { error = "Notification not found" });
            if (!n.IsRead) { n.IsRead = true; n.ReadAt = DateTime.UtcNow; await db.SaveChangesAsync(); }
            return Results.NoContent();
        });

        // POST mark all of the caller's unread notifications read. Returns the number cleared.
        grp.MapPost("/read-all", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var uid = me.Id();
            var now = DateTime.UtcNow;
            var cleared = await db.Notifications
                .Where(x => x.RecipientUserId == uid && !x.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRead, true).SetProperty(x => x.ReadAt, now));
            return Results.Ok(new { cleared });
        });
    }
}
