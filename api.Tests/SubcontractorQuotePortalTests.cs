using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.6 — Subcontractor quote portal. Proves: an estimator mints an RFQ + token;
/// an ANONYMOUS request (no auth, no tenant header) reads and submits a price via
/// the token alone (tenant resolved from the globally-unique token); the lifecycle
/// guards (one submission, expiry, revoke) hold; the estimator accepts/declines;
/// and the projects-module permission gate + cross-tenant isolation are enforced.
/// </summary>
[Collection("api")]
public class SubcontractorQuotePortalTests(ApiFixture fx)
{
    private static async Task<JsonElement> CreateInviteAsync(HttpClient admin, int pid, string trade = "Concrete works")
    {
        var r = await admin.PostAsJsonAsync("/api/subcontractor-quotes", new
        {
            projectId = pid,
            trade,
            scope = "Supply, place and finish 500 m³ of C40 concrete to raft foundation.",
            contractorName = "Acme Concrete LLC",
            contractorEmail = "estimating@acme.test",
            currency = "AED",
            validDays = 30,
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Create_returns_token_and_portal_link()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var dto = await CreateInviteAsync(admin, pid);

        Assert.Equal("Pending", dto.GetProperty("status").GetString());
        var token = dto.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(token!.Length >= 40);
        Assert.Equal($"/portal/{token}", dto.GetProperty("portalPath").GetString());
        Assert.False(dto.GetProperty("expired").GetBoolean());
    }

    [Fact]
    public async Task Anonymous_token_holder_views_and_submits_no_auth_no_tenant_header()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var token = (await CreateInviteAsync(admin, pid, "MEP — HVAC")).GetProperty("token").GetString()!;

        // No Authorization header AND no X-Tenant-Id — the token alone identifies the tenant.
        var anon = fx.AnonymousClient();

        var view = await (await anon.GetAsync($"/api/portal/{token}")).Json();
        Assert.Equal("MEP — HVAC", view.GetProperty("trade").GetString());
        Assert.Equal("Pending", view.GetProperty("status").GetString());
        Assert.False(view.GetProperty("expired").GetBoolean());
        Assert.True(view.TryGetProperty("companyName", out _));

        var submit = await anon.PostAsJsonAsync($"/api/portal/{token}",
            new { amount = 187500.50m, notes = "Includes pump + 2 crews", respondentName = "Sam Mechanic" });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var after = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Submitted", after.GetProperty("status").GetString());
        Assert.Equal(187500.50m, after.GetProperty("quotedAmount").GetDecimal());

        // The estimator now sees the priced submission in their register.
        var rows = await admin.GetFromJsonAsync<JsonElement>($"/api/subcontractor-quotes?projectId={pid}");
        var row = rows.EnumerateArray().First(x => x.GetProperty("token").GetString() == token);
        Assert.Equal("Submitted", row.GetProperty("status").GetString());
        Assert.Equal("Sam Mechanic", row.GetProperty("respondentName").GetString());
        Assert.Equal(187500.50m, row.GetProperty("quotedAmount").GetDecimal());
    }

    [Fact]
    public async Task Second_submission_is_rejected()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var token = (await CreateInviteAsync(admin, pid)).GetProperty("token").GetString()!;
        var anon = fx.AnonymousClient();

        var first = await anon.PostAsJsonAsync($"/api/portal/{token}", new { amount = 1000m, respondentName = "First" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await anon.PostAsJsonAsync($"/api/portal/{token}", new { amount = 2000m, respondentName = "Second" });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Invalid_inputs_are_rejected()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var token = (await CreateInviteAsync(admin, pid)).GetProperty("token").GetString()!;
        var anon = fx.AnonymousClient();

        var zero = await anon.PostAsJsonAsync($"/api/portal/{token}", new { amount = 0m, respondentName = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        var noName = await anon.PostAsJsonAsync($"/api/portal/{token}", new { amount = 100m, respondentName = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        var unknown = await anon.GetAsync("/api/portal/not-a-real-token");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Revoked_invite_rejects_submission()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var dto = await CreateInviteAsync(admin, pid);
        var id = dto.GetProperty("id").GetInt32();
        var token = dto.GetProperty("token").GetString()!;

        var revoke = await admin.DeleteAsync($"/api/subcontractor-quotes/{id}");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal("Revoked", (await revoke.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var anon = fx.AnonymousClient();
        var submit = await anon.PostAsJsonAsync($"/api/portal/{token}", new { amount = 5000m, respondentName = "Late" });
        Assert.Equal(HttpStatusCode.BadRequest, submit.StatusCode);
    }

    [Fact]
    public async Task Expired_invite_rejects_submission()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var token = (await CreateInviteAsync(admin, pid)).GetProperty("token").GetString()!;

        // Force the invite into the past, below the HTTP layer.
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            var q = await db.SubcontractorQuotes.FirstAsync(s => s.Token == token);
            q.ExpiresAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        });

        var anon = fx.AnonymousClient();
        var view = await (await anon.GetAsync($"/api/portal/{token}")).Json();
        Assert.True(view.GetProperty("expired").GetBoolean());

        var submit = await anon.PostAsJsonAsync($"/api/portal/{token}", new { amount = 5000m, respondentName = "Late" });
        Assert.Equal(HttpStatusCode.BadRequest, submit.StatusCode);
    }

    [Fact]
    public async Task Estimator_accepts_a_submitted_quote()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var dto = await CreateInviteAsync(admin, pid);
        var id = dto.GetProperty("id").GetInt32();
        var token = dto.GetProperty("token").GetString()!;

        // Decision before submission is rejected.
        var early = await admin.PostAsJsonAsync($"/api/subcontractor-quotes/{id}/decision", new { accept = true });
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);

        await fx.AnonymousClient().PostAsJsonAsync($"/api/portal/{token}", new { amount = 9000m, respondentName = "Bidder" });

        var accept = await admin.PostAsJsonAsync($"/api/subcontractor-quotes/{id}/decision", new { accept = true });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal("Accepted", (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Non_admin_without_project_permission_cannot_create()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);

        var email = $"sub-portal-noperm-{Guid.NewGuid():N}@bidbuilder.local";
        (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = email, email, password = "Pw@123456", role = "TenantUser", groupIds = Array.Empty<int>() }))
            .EnsureSuccessStatusCode();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");

        var r = await user.PostAsJsonAsync("/api/subcontractor-quotes", new
        {
            projectId = pid, trade = "T", scope = "S", contractorName = "C", validDays = 7,
        });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Token_is_isolated_from_other_tenants_register()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var token = (await CreateInviteAsync(admin, pid)).GetProperty("token").GetString()!;

        // A different tenant's admin never sees this RFQ in their own register…
        var other = await fx.SecondTenantAdminClientAsync();
        var rows = await other.GetFromJsonAsync<JsonElement>("/api/subcontractor-quotes");
        Assert.DoesNotContain(rows.EnumerateArray(), x => x.GetProperty("token").GetString() == token);

        // …yet the public portal still resolves it by token (tenant from the token).
        var view = await (await fx.AnonymousClient().GetAsync($"/api/portal/{token}")).Json();
        Assert.Equal("Pending", view.GetProperty("status").GetString());
    }
}
