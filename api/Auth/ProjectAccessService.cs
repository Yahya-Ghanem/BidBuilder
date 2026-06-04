using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Auth;

/// <summary>
/// Centralises "which projects may this user touch" — the project scoping layer.
/// Admins (TenantAdmin/SuperAdmin) see every project in the tenant; everyone else
/// only projects one of their teams (Groups) is assigned to via ProjectTeam.
/// Both ProjectTeams and UserGroups are already tenant-scoped by query filter.
/// </summary>
public class ProjectAccessService(AppDbContext db)
{
    public IQueryable<Project> Accessible(ClaimsPrincipal user)
    {
        if (user.IsAdmin()) return db.Projects;

        var uid = user.Id();
        var myGroupIds = db.UserGroups.Where(ug => ug.UserId == uid).Select(ug => ug.GroupId);
        return db.Projects.Where(p =>
            db.ProjectTeams.Any(pt => pt.ProjectId == p.Id && myGroupIds.Contains(pt.GroupId)));
    }

    public Task<bool> CanAccessProjectAsync(ClaimsPrincipal user, int projectId) =>
        Accessible(user).AnyAsync(p => p.Id == projectId);

    /// <summary>Project access for the project that owns the given estimate.</summary>
    public async Task<bool> CanAccessEstimateAsync(ClaimsPrincipal user, int estimateId)
    {
        var projectId = await db.Estimates.Where(e => e.Id == estimateId)
                                          .Select(e => (int?)e.ProjectId).FirstOrDefaultAsync();
        return projectId is not null && await CanAccessProjectAsync(user, projectId.Value);
    }
}
