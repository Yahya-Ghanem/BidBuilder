using System.Security.Claims;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// Writes the per-tenant audit trail. Call <see cref="LogAsync"/> AFTER an
/// operation's own SaveChanges (so the event records committed work and its own
/// save carries no other pending changes). Failures are swallowed — auditing must
/// never break the action being audited. TenantId is auto-stamped by AppDbContext.
/// </summary>
public class AuditService(AppDbContext db, ILogger<AuditService> logger)
{
    public async Task LogAsync(ClaimsPrincipal user, string action, string entity, string? entityKey, string? summary = null)
    {
        try
        {
            db.AuditEvents.Add(new AuditEvent
            {
                ActorEmail = user.Email(),
                ActorName  = user.Name(),
                ActorRole  = user.Role().ToString(),
                Action     = action,
                Entity     = entity,
                EntityKey  = entityKey,
                Summary    = summary,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audit write failed: {Action} {Entity} {Key}", action, entity, entityKey);
        }
    }
}
