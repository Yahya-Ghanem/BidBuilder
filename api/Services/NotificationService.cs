using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// 20.3 — Creates in-app notifications and resolves their audiences. Like
/// <see cref="AuditService"/>, call <see cref="NotifyAsync"/> AFTER the
/// triggering operation's own SaveChanges, and failures are swallowed: a
/// notification must never break the action that prompted it. TenantId is
/// auto-stamped by <see cref="AppDbContext"/>.
/// </summary>
public class NotificationService(AppDbContext db, ILogger<NotificationService> logger)
{
    /// <summary>Fan a single event out to a set of recipients (deduped; invalid
    /// ids dropped). A no-op when the audience is empty.</summary>
    public async Task NotifyAsync(IEnumerable<int> recipientUserIds, string type, string title,
        string? body = null, string? link = null, string? entityType = null, string? entityKey = null)
    {
        try
        {
            var ids = recipientUserIds.Where(id => id > 0).Distinct().ToList();
            if (ids.Count == 0) return;
            foreach (var uid in ids)
                db.Notifications.Add(new Notification
                {
                    RecipientUserId = uid, Type = type, Title = title, Body = body,
                    Link = link, EntityType = entityType, EntityKey = entityKey,
                });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification write failed: {Type} {EntityKey}", type, entityKey);
        }
    }

    /// <summary>Active TenantAdmins in the current tenant (the approver/coordinator
    /// audience), optionally excluding the actor who triggered the event.</summary>
    public Task<List<int>> TenantAdminIdsAsync(int? exclude = null) =>
        db.Users
            .Where(u => u.IsActive && u.Role == UserRole.TenantAdmin)
            .Where(u => exclude == null || u.Id != exclude)
            .Select(u => u.Id)
            .ToListAsync();

    /// <summary>Everyone who should hear about a project event: active members of
    /// any team assigned to the project, UNION the tenant's admins. The actor is
    /// excluded so you never get notified about your own action.</summary>
    public async Task<List<int>> ProjectAudienceIdsAsync(int projectId, int? exclude = null)
    {
        var groupIds = await db.ProjectTeams
            .Where(pt => pt.ProjectId == projectId)
            .Select(pt => pt.GroupId)
            .ToListAsync();
        var teamUsers = await db.UserGroups
            .Where(ug => groupIds.Contains(ug.GroupId) && ug.User.IsActive)
            .Select(ug => ug.UserId)
            .ToListAsync();
        var admins = await TenantAdminIdsAsync();
        return teamUsers.Concat(admins)
            .Where(id => exclude == null || id != exclude)
            .Distinct()
            .ToList();
    }
}
