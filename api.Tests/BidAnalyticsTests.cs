using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 19.1 coverage. The bid register + win-rate dashboard is the project's first
/// real estimating-maturity loop, so the things that MUST be true:
///   1. The bid-outcome PATCH writes only the supplied fields (true partial
///      update — null = leave alone; clear-note flag wipes).
///   2. The analytics roll-up reports hit-rate, bid-vs-award variance, and
///      per-bucket counts correctly on a hand-built fixture.
///   3. Access is scoped: a user only sees bid data for projects they can
///      already see via ProjectAccessService (no analytics back-door).
///
/// All three tests build their own tenant + projects so they don't depend on
/// (or pollute) the seeded baseline.
/// </summary>
[Collection("api")]
public class BidAnalyticsTests(ApiFixture fx)
{
    [Fact]
    public async Task Bid_outcome_patch_records_partial_fields_and_clears_note_explicitly()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);

        // Step 1: set everything in one go.
        var resp = await admin.PatchAsJsonAsync($"/api/projects/{pid}/bid-outcome", new
        {
            submittedBidValue = 100_000m,
            awardedValue      = 95_000m,
            finalCost         = 90_000m,
            decisionAt        = "2026-03-15T00:00:00Z",
            winLossNote       = "Lost on price — undercut 5%",
        });
        resp.EnsureSuccessStatusCode();
        var bd = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100_000m, bd.GetProperty("submittedBidValue").GetDecimal());
        Assert.Equal(95_000m,  bd.GetProperty("awardedValue").GetDecimal());
        Assert.Equal(90_000m,  bd.GetProperty("finalCost").GetDecimal());
        Assert.Equal("Lost on price — undercut 5%", bd.GetProperty("winLossNote").GetString());

        // Step 2: send ONLY a finalCost update; everything else must stay put.
        var partial = await admin.PatchAsJsonAsync($"/api/projects/{pid}/bid-outcome", new
        {
            finalCost = 88_500m,
        });
        partial.EnsureSuccessStatusCode();
        var bd2 = await partial.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100_000m, bd2.GetProperty("submittedBidValue").GetDecimal());  // untouched
        Assert.Equal(95_000m,  bd2.GetProperty("awardedValue").GetDecimal());        // untouched
        Assert.Equal(88_500m,  bd2.GetProperty("finalCost").GetDecimal());           // updated
        Assert.Equal("Lost on price — undercut 5%", bd2.GetProperty("winLossNote").GetString());

        // Step 3: clear the note. ClearWinLossNote=true wipes even though winLossNote is null.
        var cleared = await admin.PatchAsJsonAsync($"/api/projects/{pid}/bid-outcome", new
        {
            clearWinLossNote = true,
        });
        cleared.EnsureSuccessStatusCode();
        var bd3 = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(bd3.GetProperty("winLossNote").ValueKind == JsonValueKind.Null);

        // Validation: negative bid → 400, and existing values unchanged.
        var bad = await admin.PatchAsJsonAsync($"/api/projects/{pid}/bid-outcome", new
        {
            submittedBidValue = -1m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Bid_analytics_rolls_up_hit_rate_and_bid_vs_award_variance_correctly()
    {
        // Build a fresh fixture: 4 decided projects in 2026-Q1.
        //   • Won × 2  (bid 100 → award 95;  bid 200 → award 220)
        //   • Lost × 2 (bid 50; bid 80)
        // Expected overall:
        //   total=4, won=2, lost=2, hitRate=50.00%, avgBidVsAwardPct = avg(-5, 10) = 2.50%,
        //   awardedValueSum = 95 + 220 = 315
        var stamp = System.Guid.NewGuid().ToString("N")[..6];
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            var projects = new[]
            {
                Make($"BR-WON1-{stamp}",  "Acme", ProjectStatus.Won,  100m, 95m,  new DateTime(2026,1,10,0,0,0,DateTimeKind.Utc)),
                Make($"BR-WON2-{stamp}",  "Acme", ProjectStatus.Won,  200m, 220m, new DateTime(2026,2,1, 0,0,0,DateTimeKind.Utc)),
                Make($"BR-LOST1-{stamp}", "Beta", ProjectStatus.Lost, 50m,  null, new DateTime(2026,2,20,0,0,0,DateTimeKind.Utc)),
                Make($"BR-LOST2-{stamp}", "Beta", ProjectStatus.Lost, 80m,  null, new DateTime(2026,3,5, 0,0,0,DateTimeKind.Utc)),
            };
            db.Projects.AddRange(projects);
            await db.SaveChangesAsync();
        });

        var admin = await fx.AdminClientAsync();
        var bd = await admin.GetFromJsonAsync<JsonElement>("/api/analytics/bids?from=2026-01-01&to=2026-03-31");

        // Overall — be generous to ANY other decided project that already lived in the seed,
        // but the FOUR we just added must be inside it.
        var overall = bd.GetProperty("overall");
        Assert.True(overall.GetProperty("won").GetInt32() >= 2);
        Assert.True(overall.GetProperty("lost").GetInt32() >= 2);

        // Pull the by-client bucket for "Acme" — strictly our two Wons.
        var acme = bd.GetProperty("byClient").EnumerateArray().FirstOrDefault(b => b.GetProperty("key").GetString() == "Acme");
        Assert.NotEqual(default, acme);
        Assert.Equal(2,    acme.GetProperty("total").GetInt32());
        Assert.Equal(2,    acme.GetProperty("won").GetInt32());
        Assert.Equal(0,    acme.GetProperty("lost").GetInt32());
        Assert.Equal(100m, acme.GetProperty("hitRatePct").GetDecimal());
        // avg(-5, 10) = 2.50
        Assert.Equal(2.50m, acme.GetProperty("avgBidVsAwardPct").GetDecimal());
        Assert.Equal(315m,  acme.GetProperty("awardedValueSum").GetDecimal());

        // by-client "Beta" — two Losts, hitRate 0, no variance (no awarded).
        var beta = bd.GetProperty("byClient").EnumerateArray().FirstOrDefault(b => b.GetProperty("key").GetString() == "Beta");
        Assert.NotEqual(default, beta);
        Assert.Equal(2, beta.GetProperty("total").GetInt32());
        Assert.Equal(0, beta.GetProperty("won").GetInt32());
        Assert.Equal(0m, beta.GetProperty("hitRatePct").GetDecimal());
        Assert.True(beta.GetProperty("avgBidVsAwardPct").ValueKind == JsonValueKind.Null);

        // Period buckets exist for 2026-Q1.
        var q1 = bd.GetProperty("byPeriod").EnumerateArray().FirstOrDefault(b => b.GetProperty("key").GetString() == "2026-Q1");
        Assert.NotEqual(default, q1);
        Assert.True(q1.GetProperty("total").GetInt32() >= 4);
    }

    [Fact]
    public async Task Bid_analytics_is_scoped_to_projects_the_user_can_access()
    {
        // A user in Team A only sees projects assigned to Team A. The analytics endpoint
        // MUST honour the same scope — recording Team B's wins shouldn't leak through.
        var stamp = System.Guid.NewGuid().ToString("N")[..6];
        int teamAId = 0, teamBId = 0;
        await fx.WithTenantDbAsync("default", async (db, tenant) =>
        {
            var teamA = new Group { Code = $"TM-A-{stamp}", Name = "Team A " + stamp };
            var teamB = new Group { Code = $"TM-B-{stamp}", Name = "Team B " + stamp };
            db.Groups.AddRange(teamA, teamB); await db.SaveChangesAsync();
            teamAId = teamA.Id; teamBId = teamB.Id;

            var pwd = BCrypt.Net.BCrypt.HashPassword("Test@12345");
            // User isn't IHasTenant (TenantId is nullable for SuperAdmin), so the
            // SaveChanges auto-stamp doesn't fire — set the tenant explicitly.
            // Email MUST be lowercase — the auth handler lowers req.Email before lookup.
            var userA = new User { TenantId = tenant.Id,
                Email = $"estimator-{stamp}@bidbuilder.local", Name = "EstA",
                Role = UserRole.TenantUser, IsActive = true, PasswordHash = pwd };
            db.Users.Add(userA); await db.SaveChangesAsync();
            db.UserGroups.Add(new UserGroup { UserId = userA.Id, GroupId = teamA.Id });
            await db.SaveChangesAsync();

            var hiddenProj = new Project { Code = $"BSCOPE-B-{stamp}", Name = "Hidden B", ClientName = "ZetaCorp",
                Currency = "AED", Status = ProjectStatus.Won,
                SubmittedBidValue = 9_999m, AwardedValue = 10_000m,
                DecisionAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) };
            db.Projects.Add(hiddenProj); await db.SaveChangesAsync();
            db.ProjectTeams.Add(new ProjectTeam { ProjectId = hiddenProj.Id, GroupId = teamB.Id, IsLead = true });
            await db.SaveChangesAsync();
        });

        var userAClient = await fx.AuthedClientAsync($"estimator-{stamp}@bidbuilder.local", "Test@12345");
        var bd = await userAClient.GetFromJsonAsync<JsonElement>("/api/analytics/bids?from=2026-04-01&to=2026-04-30");

        // ZetaCorp's win belongs to Team B → MUST NOT appear in user-A's analytics.
        var clients = bd.GetProperty("byClient").EnumerateArray().Select(b => b.GetProperty("key").GetString()).ToList();
        Assert.DoesNotContain("ZetaCorp", clients);
        var codes = bd.GetProperty("register").EnumerateArray().Select(r => r.GetProperty("code").GetString()).ToList();
        Assert.DoesNotContain(codes, c => c!.StartsWith("BSCOPE-B"));
    }

    private static Project Make(string code, string client, ProjectStatus status, decimal? bid, decimal? award, DateTime decision)
        => new Project
        {
            Code = code, Name = code, ClientName = client, Currency = "AED",
            Status = status, SubmittedBidValue = bid, AwardedValue = award,
            DecisionAt = decision,
        };
}
