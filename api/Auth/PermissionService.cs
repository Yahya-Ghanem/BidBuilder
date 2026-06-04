using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;

namespace BidBuilder.Api.Auth;

public enum ModuleAction { View, Add, Edit, Delete }

/// <summary>
/// Resolves whether the current user may perform an action on a module, using
/// the reused RBAC core: a user's teams (Groups) → GroupModule flags. Admins
/// bypass the check (full access). This is what makes the seeded Modules
/// (projects, resource-library, boq …) actually govern behaviour.
/// </summary>
public class PermissionService(AppDbContext db)
{
    public async Task<bool> CanAsync(ClaimsPrincipal user, string moduleCode, ModuleAction action)
    {
        if (user.IsAdmin()) return true;

        var uid = user.Id();
        var flags = await (
            from ug in db.UserGroups.Where(x => x.UserId == uid)
            join gm in db.GroupModules on ug.GroupId equals gm.GroupId
            join m  in db.Modules      on gm.ModuleId equals m.Id
            where m.Code == moduleCode
            select gm).ToListAsync();

        return action switch
        {
            ModuleAction.View   => flags.Any(f => f.CanView),
            ModuleAction.Add    => flags.Any(f => f.CanAdd),
            ModuleAction.Edit   => flags.Any(f => f.CanEdit),
            ModuleAction.Delete => flags.Any(f => f.CanDelete),
            _ => false,
        };
    }
}
