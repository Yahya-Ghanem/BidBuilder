using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

public record ProjectDto(
    int Id, string Code, string Name, string? ClientName, string? Location,
    string Currency, string Status, int? DurationMonths, DateTime? TenderDueAt,
    int TeamCount, int EstimateCount, int? ProjectTypeId, string? ProjectTypeName);

public record CreateProjectRequest(
    string Name, string? Code, string? ClientName, string? Location,
    string? Currency, int? DurationMonths, DateTime? TenderDueAt, int? ProjectTypeId);

public record UpdateProjectRequest(
    string Name, string? ClientName, string? Location,
    string? Currency, int? DurationMonths, DateTime? TenderDueAt, string? Status, int? ProjectTypeId);

public record AssignTeamRequest(int GroupId, bool IsLead);
public record ProjectTeamDto(int GroupId, string GroupCode, string GroupName, bool IsLead);
public record GroupOptionDto(int Id, string Code, string Name, int MemberCount, bool IsBuiltIn);

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/projects").RequireAuthorization();

        // GET /api/projects — projects the caller may see (admins: all in tenant;
        // others: only those their teams are assigned to).
        grp.MapGet("/", async (ClaimsPrincipal me, ProjectAccessService access, AppDbContext db) =>
        {
            var list = await access.Accessible(me)
                .OrderByDescending(p => p.UpdatedAt)
                .Select(p => new ProjectDto(
                    p.Id, p.Code, p.Name, p.ClientName, p.Location, p.Currency,
                    p.Status.ToString(), p.DurationMonths, p.TenderDueAt,
                    p.ProjectTeams.Count, p.Estimates.Count, p.ProjectTypeId,
                    db.ProjectTypes.Where(t => t.Id == p.ProjectTypeId).Select(t => t.Name).FirstOrDefault()))
                .ToListAsync();
            return Results.Ok(list);
        });

        // GET /api/projects/{id} — counts are projected in SQL (the nav collections
        // aren't loaded, so an in-memory .Count would always be 0).
        grp.MapGet("/{id:int}", async (int id, ClaimsPrincipal me, ProjectAccessService access, AppDbContext db) =>
        {
            var dto = await access.Accessible(me)
                .Where(x => x.Id == id)
                .Select(p => new ProjectDto(
                    p.Id, p.Code, p.Name, p.ClientName, p.Location, p.Currency,
                    p.Status.ToString(), p.DurationMonths, p.TenderDueAt,
                    p.ProjectTeams.Count, p.Estimates.Count, p.ProjectTypeId,
                    db.ProjectTypes.Where(t => t.Id == p.ProjectTypeId).Select(t => t.Name).FirstOrDefault()))
                .FirstOrDefaultAsync();
            return dto is null
                ? Results.NotFound(new { error = "Project not found or not accessible" })
                : Results.Ok(dto);
        });

        // POST /api/projects — create (TenantAdmin only).
        grp.MapPost("/", async (CreateProjectRequest req, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can create projects" }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required" });

            var code = string.IsNullOrWhiteSpace(req.Code)
                ? await NextCode(db)
                : req.Code!.Trim();

            if (await db.Projects.AnyAsync(p => p.Code == code))
                return Results.Conflict(new { error = $"Project code '{code}' already exists" });

            if (req.ProjectTypeId is int tid && !await db.ProjectTypes.AnyAsync(t => t.Id == tid))
                return Results.BadRequest(new { error = "Unknown project type" });

            var project = new Project
            {
                Code = code,
                Name = req.Name.Trim(),
                ClientName = req.ClientName,
                Location = req.Location,
                Currency = string.IsNullOrWhiteSpace(req.Currency) ? "AED" : req.Currency!.Trim(),
                DurationMonths = req.DurationMonths,
                TenderDueAt = req.TenderDueAt,
                ProjectTypeId = req.ProjectTypeId,
                Status = ProjectStatus.Draft,
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "project.create", "Project", project.Code, project.Name);

            var typeName = await TypeName(db, project.ProjectTypeId);
            return Results.Created($"/api/projects/{project.Id}", new ProjectDto(
                project.Id, project.Code, project.Name, project.ClientName, project.Location,
                project.Currency, project.Status.ToString(), project.DurationMonths,
                project.TenderDueAt, 0, 0, project.ProjectTypeId, typeName));
        });

        // PUT /api/projects/{id} — edit project details (TenantAdmin only). Changing
        // the duration re-prices the project's time-related preliminaries, so its
        // non-locked estimates are recomputed (Published/Superseded stay frozen).
        grp.MapPut("/{id:int}", async (int id, UpdateProjectRequest req, ClaimsPrincipal me, AppDbContext db, EstimateCalculator calc, AuditService audit) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can edit projects" }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required" });

            var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound(new { error = "Project not found" });

            if (req.ProjectTypeId is int tid && !await db.ProjectTypes.AnyAsync(t => t.Id == tid))
                return Results.BadRequest(new { error = "Unknown project type" });

            var durationChanged = p.DurationMonths != req.DurationMonths;

            p.Name = req.Name.Trim();
            p.ClientName = string.IsNullOrWhiteSpace(req.ClientName) ? null : req.ClientName.Trim();
            p.Location = string.IsNullOrWhiteSpace(req.Location) ? null : req.Location.Trim();
            if (!string.IsNullOrWhiteSpace(req.Currency)) p.Currency = req.Currency.Trim();
            p.DurationMonths = req.DurationMonths;
            p.TenderDueAt = req.TenderDueAt;
            p.ProjectTypeId = req.ProjectTypeId;
            if (!string.IsNullOrWhiteSpace(req.Status))
            {
                if (!Enum.TryParse<ProjectStatus>(req.Status, true, out var st))
                    return Results.BadRequest(new { error = $"Invalid status '{req.Status}'" });
                p.Status = st;
            }
            p.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            if (durationChanged)
            {
                var ids = await db.Estimates
                    .Where(e => e.ProjectId == id && e.Status != EstimateStatus.Published && e.Status != EstimateStatus.Superseded)
                    .Select(e => e.Id).ToListAsync();
                foreach (var eid in ids) await calc.RecomputeAsync(eid);
            }
            await audit.LogAsync(me, "project.update", "Project", p.Code, $"status={p.Status}, duration={p.DurationMonths?.ToString() ?? "—"}mo");

            return Results.Ok(new ProjectDto(
                p.Id, p.Code, p.Name, p.ClientName, p.Location, p.Currency, p.Status.ToString(),
                p.DurationMonths, p.TenderDueAt,
                await db.ProjectTeams.CountAsync(t => t.ProjectId == id),
                await db.Estimates.CountAsync(e => e.ProjectId == id),
                p.ProjectTypeId, await TypeName(db, p.ProjectTypeId)));
        });

        // GET /api/projects/groups — all teams in the tenant, for the assignment
        // dropdown (admin only). The :int constraint on /{id} keeps "groups" distinct.
        grp.MapGet("/groups", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can list teams" }, statusCode: 403);

            var groups = await db.Groups
                .OrderBy(g => g.Name)
                .Select(g => new GroupOptionDto(g.Id, g.Code, g.Name, g.MemberCount, g.IsBuiltIn))
                .ToListAsync();
            return Results.Ok(groups);
        });

        // GET /api/projects/{id}/teams
        grp.MapGet("/{id:int}/teams", async (int id, ClaimsPrincipal me, ProjectAccessService access, AppDbContext db) =>
        {
            if (!await access.Accessible(me).AnyAsync(p => p.Id == id))
                return Results.NotFound(new { error = "Project not found or not accessible" });

            var teams = await db.ProjectTeams
                .Where(pt => pt.ProjectId == id)
                .Select(pt => new ProjectTeamDto(pt.GroupId, pt.Group.Code, pt.Group.Name, pt.IsLead))
                .ToListAsync();
            return Results.Ok(teams);
        });

        // POST /api/projects/{id}/teams — assign a team to a project (admin).
        grp.MapPost("/{id:int}/teams", async (int id, AssignTeamRequest req, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can assign teams" }, statusCode: 403);

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
            if (project is null) return Results.NotFound(new { error = "Project not found" });

            var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == req.GroupId);
            if (group is null) return Results.NotFound(new { error = "Team (group) not found" });

            if (await db.ProjectTeams.AnyAsync(pt => pt.ProjectId == id && pt.GroupId == req.GroupId))
                return Results.Conflict(new { error = "Team already assigned to this project" });

            db.ProjectTeams.Add(new ProjectTeam { ProjectId = id, GroupId = req.GroupId, IsLead = req.IsLead });
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "project.team.assign", "Project", project.Code, $"team {group.Code}{(req.IsLead ? " (lead)" : "")}");
            return Results.Ok(new ProjectTeamDto(group.Id, group.Code, group.Name, req.IsLead));
        });

        // DELETE /api/projects/{id}/teams/{groupId} — unassign a team (admin only).
        grp.MapDelete("/{id:int}/teams/{groupId:int}", async (int id, int groupId, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can remove teams" }, statusCode: 403);

            var pt = await db.ProjectTeams.Include(x => x.Group).FirstOrDefaultAsync(x => x.ProjectId == id && x.GroupId == groupId);
            if (pt is null) return Results.NotFound(new { error = "Team is not assigned to this project" });

            db.ProjectTeams.Remove(pt);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "project.team.remove", "Project", id.ToString(), $"team {pt.Group.Code}");
            return Results.NoContent();
        });
    }

    private static async Task<string> NextCode(AppDbContext db)
    {
        // PRJ-<year>-NNN, sequential within the tenant for the current year.
        var year = DateTime.UtcNow.Year;
        var prefix = $"PRJ-{year}-";
        var count = await db.Projects.CountAsync(p => p.Code.StartsWith(prefix));
        return $"{prefix}{(count + 1):D3}";
    }

    private static async Task<string?> TypeName(AppDbContext db, int? typeId) =>
        typeId is int id ? await db.ProjectTypes.Where(t => t.Id == id).Select(t => t.Name).FirstOrDefaultAsync() : null;
}
