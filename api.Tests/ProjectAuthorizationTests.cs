using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// Proves the project scoping layer: a non-admin user only reaches projects one of
/// their teams is assigned to (via ProjectTeam). Cross-team access is denied with a
/// 404 (existence-hiding) across every project-scoped surface — project, estimates,
/// BOQ writes, areas, area-rollup and exports. This is the intra-tenant confidentiality
/// guarantee, exercised end-to-end over HTTP.
/// </summary>
[Collection("api")]
public class ProjectAuthorizationTests(ApiFixture fx)
{
    [Fact]
    public async Task Non_admin_cannot_reach_a_project_their_team_is_not_assigned_to()
    {
        // Email MUST be lowercase — login looks the user up by email.ToLower().
        var email = $"estimator-{System.Guid.NewGuid():N}@bidbuilder.local";
        const string pw = "Estimator@123";
        int projectA = 0, projectB = 0, estimateB = 0;

        await fx.WithTenantDbAsync("default", async (db, tenant) =>
        {
            var teamA = new Group { Code = $"TA-{System.Guid.NewGuid():N}"[..10], Name = "Team A" };
            var teamB = new Group { Code = $"TB-{System.Guid.NewGuid():N}"[..10], Name = "Team B" };
            db.Groups.AddRange(teamA, teamB);
            var pA = new Project { Code = $"PA-{System.Guid.NewGuid():N}"[..12], Name = "Proj A", Currency = "AED", Status = ProjectStatus.Draft };
            var pB = new Project { Code = $"PB-{System.Guid.NewGuid():N}"[..12], Name = "Proj B", Currency = "AED", Status = ProjectStatus.Draft };
            db.Projects.AddRange(pA, pB);
            await db.SaveChangesAsync();

            // User is NOT IHasTenant → its TenantId is set explicitly.
            db.Users.Add(new User { TenantId = tenant.Id, Email = email, Name = "Estimator A", Role = UserRole.TenantUser, IsActive = true, PasswordHash = BCrypt.Net.BCrypt.HashPassword(pw) });
            db.ProjectTeams.Add(new ProjectTeam { ProjectId = pA.Id, GroupId = teamA.Id });   // A → Team A
            db.ProjectTeams.Add(new ProjectTeam { ProjectId = pB.Id, GroupId = teamB.Id });   // B → Team B (NOT the user's team)
            var estB = new Estimate { ProjectId = pB.Id, Revision = 1, Title = "B rev 1", Status = EstimateStatus.Draft, Currency = "AED" };
            db.Estimates.Add(estB);
            await db.SaveChangesAsync();

            var user = await db.Users.FirstAsync(u => u.Email == email);
            db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = teamA.Id });   // user ∈ Team A only
            await db.SaveChangesAsync();

            projectA = pA.Id; projectB = pB.Id; estimateB = estB.Id;
        });

        var c = await fx.AuthedClientAsync(email, pw);

        // The project list shows only Team A's project, never Team B's.
        var list = await c.GetFromJsonAsync<JsonElement>("/api/projects");
        var visible = list.EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(projectA, visible);
        Assert.DoesNotContain(projectB, visible);

        // Direct access to Team B's project is denied (404) on every surface.
        await Denied(c, HttpMethod.Get, $"/api/projects/{projectB}");
        await Denied(c, HttpMethod.Get, $"/api/projects/{projectB}/estimates");
        await Denied(c, HttpMethod.Get, $"/api/projects/{projectB}/areas");
        await Denied(c, HttpMethod.Get, $"/api/estimates/{estimateB}");
        await Denied(c, HttpMethod.Get, $"/api/estimates/{estimateB}/areas-rollup");
        await Denied(c, HttpMethod.Get, $"/api/estimates/{estimateB}/export.xlsx");

        // A WRITE against Team B's estimate is denied too (not merely reads).
        var write = await c.PostAsJsonAsync($"/api/estimates/{estimateB}/sections", new { code = "X", title = "X", sortOrder = 1 });
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);

        // Positive control: the user's OWN project is reachable (so the test isn't vacuously denying).
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/projects/{projectA}")).StatusCode);
    }

    private static async Task Denied(HttpClient c, HttpMethod method, string url)
    {
        var resp = await c.SendAsync(new HttpRequestMessage(method, url));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
