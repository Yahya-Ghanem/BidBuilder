using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.2 — Tests for the publish-approval gate.
///
/// Coverage:
///   • POST /approvals records a sign-off and is idempotent for the same user.
///   • PUT meta status=Published is BLOCKED (409 with structured payload) when
///     fewer approvals than the tenant requires are recorded.
///   • Sufficient approvals unblock the publish.
///   • Any BOQ content edit (add a section here) wipes ALL approvals — a
///     publish on the post-edit revision must collect fresh sign-offs.
///   • A plain TenantUser cannot approve (403).
///   • Setting RequiredApprovalsToPublish=0 in tenant settings restores the
///     legacy "anyone with permission can publish" path.
/// </summary>
[Collection("api")]
public class ApprovalWorkflowTests(ApiFixture fx)
{
    /// <summary>PUT /api/settings with RequiredApprovalsToPublish set.
    /// Keeps the other defaults at zero — settings is a full-record PUT.</summary>
    static Task<HttpResponseMessage> SetRequiredApprovals(HttpClient admin, int n) =>
        admin.PutAsJsonAsync("/api/settings/", new
        {
            timezone = "UTC", baseCurrency = "AED",
            defaultOverheadPct = 0m, defaultProfitPct = 0m,
            defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            requiredApprovalsToPublish = n,
        });

    [Fact]
    public async Task Publish_is_blocked_with_409_when_required_approvals_not_met()
    {
        var admin = await fx.AdminClientAsync();
        (await SetRequiredApprovals(admin, 1)).EnsureSuccessStatusCode();

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "approval-block");

        // Publish attempt with ZERO approvals → 409 with structured payload.
        var resp = await Api.PutMetaAsync(admin, eid, new { status = "Published" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("requiredApprovals").GetInt32());
        Assert.Equal(0, body.GetProperty("currentApprovals").GetInt32());

        // Status didn't sneak through.
        var bd = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        Assert.Equal("Draft", bd.GetProperty("status").GetString());

        // Reset so we don't leak the requirement into other tests in the collection.
        (await SetRequiredApprovals(admin, 0)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Approval_is_idempotent_and_unblocks_publish_when_threshold_met()
    {
        var admin = await fx.AdminClientAsync();
        (await SetRequiredApprovals(admin, 1)).EnsureSuccessStatusCode();

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "approval-unblock");

        // Two POSTs by the same admin yield count=1 (idempotent).
        var first  = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/approvals", new { note = "ok" })).Json();
        var second = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/approvals", new { note = "again" })).Json();
        Assert.Equal(1, first.GetProperty("currentApprovals").GetInt32());
        Assert.Equal(1, second.GetProperty("currentApprovals").GetInt32());
        Assert.Equal(1, second.GetProperty("approvals").GetArrayLength());

        // Now the publish flies.
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();
        var bd = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        Assert.Equal("Published", bd.GetProperty("status").GetString());

        (await SetRequiredApprovals(admin, 0)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Bid_content_edit_wipes_existing_approvals()
    {
        var admin = await fx.AdminClientAsync();
        (await SetRequiredApprovals(admin, 1)).EnsureSuccessStatusCode();

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "approval-wipe");

        // Collect the required sign-off.
        var afterApprove = await (await admin.PostAsJsonAsync($"/api/estimates/{eid}/approvals", new { note = (string?)null })).Json();
        Assert.Equal(1, afterApprove.GetProperty("currentApprovals").GetInt32());

        // Touch the bid: add a section. The SaveChanges interceptor must wipe approvals.
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/sections",
            new { code = "S1", title = "After-approval edit", sortOrder = 0 })).EnsureSuccessStatusCode();

        var afterEdit = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/approvals");
        Assert.Equal(0, afterEdit.GetProperty("currentApprovals").GetInt32());

        // And the publish is blocked again until fresh sign-offs land.
        Assert.Equal(HttpStatusCode.Conflict, (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).StatusCode);

        (await SetRequiredApprovals(admin, 0)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Non_admin_user_cannot_approve()
    {
        var admin = await fx.AdminClientAsync();
        // The new user is added to the ADMINS group so the project-access check
        // passes — that lets us isolate the ROLE check (only TenantAdmin can
        // approve) instead of bouncing off a 404 from the access guard.
        var groups = await admin.GetFromJsonAsync<JsonElement>("/api/projects/groups");
        int adminGroupId = groups.EnumerateArray().First(g => g.GetProperty("code").GetString() == "ADMINS").GetProperty("id").GetInt32();
        const string email = "approver.user@bidbuilder.local";
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "User Bob", email, password = "Pw@123456", role = "TenantUser", groupIds = new[] { adminGroupId } })).Json();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "approval-non-admin");

        var r = await user.PostAsJsonAsync($"/api/estimates/{eid}/approvals", new { note = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task When_required_is_zero_publish_works_with_zero_approvals()
    {
        var admin = await fx.AdminClientAsync();
        (await SetRequiredApprovals(admin, 0)).EnsureSuccessStatusCode();

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "approval-legacy");
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();
        var bd = await admin.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        Assert.Equal("Published", bd.GetProperty("status").GetString());
    }
}
