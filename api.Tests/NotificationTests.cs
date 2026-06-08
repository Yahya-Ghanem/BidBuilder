using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.3 — Tests for in-app notifications.
///
/// Coverage:
///   • Publishing an estimate notifies the project audience; the actor (publisher)
///     does NOT get a self-notification.
///   • Moving to UnderReview pings the tenant admins ("approval needed").
///   • A new approval notifies the other admins.
///   • unread-count + mark-one-read + read-all behave correctly.
///   • Notifications are recipient-scoped (one user can't see another's inbox).
/// </summary>
[Collection("api")]
public class NotificationTests(ApiFixture fx)
{
    /// <summary>Create an additional TenantAdmin (a notification recipient) and return a client for them.</summary>
    async Task<HttpClient> NewAdminAsync(HttpClient admin, string email)
    {
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Recipient", email, password = "Pw@123456", role = "TenantAdmin", groupIds = Array.Empty<int>() })).Json();
        return await fx.AuthedClientAsync(email, "Pw@123456");
    }

    /// <summary>Force the tenant's publish-approval requirement to zero so a publish goes
    /// straight through (this test class isn't exercising the approval gate).</summary>
    static Task<HttpResponseMessage> ClearApprovalRequirement(HttpClient admin) =>
        admin.PutAsJsonAsync("/api/settings/", new
        {
            timezone = "UTC", baseCurrency = "AED",
            defaultOverheadPct = 0m, defaultProfitPct = 0m,
            defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            requiredApprovalsToPublish = 0,
        });

    static async Task<JsonElement> InboxAsync(HttpClient c, bool unreadOnly = false) =>
        await c.GetFromJsonAsync<JsonElement>($"/api/notifications?take=100{(unreadOnly ? "&unreadOnly=true" : "")}");

    static bool Has(JsonElement inbox, int eid, string type) =>
        inbox.GetProperty("items").EnumerateArray().Any(x =>
            x.GetProperty("entityKey").GetString() == eid.ToString() && x.GetProperty("type").GetString() == type);

    static async Task<int> UnreadCountAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/notifications/unread-count")).GetProperty("count").GetInt32();

    [Fact]
    public async Task Publishing_notifies_other_admins_but_not_the_actor()
    {
        var admin = await fx.AdminClientAsync();
        (await ClearApprovalRequirement(admin)).EnsureSuccessStatusCode();
        var other = await NewAdminAsync(admin, "notif.publish.recipient@bidbuilder.local");

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "notif-publish");
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();

        Assert.True(Has(await InboxAsync(other), eid, "estimate.published"));
        // The publisher must not be notified about their own action.
        Assert.False(Has(await InboxAsync(admin), eid, "estimate.published"));
    }

    [Fact]
    public async Task Under_review_pings_tenant_admins()
    {
        var admin = await fx.AdminClientAsync();
        var other = await NewAdminAsync(admin, "notif.review.recipient@bidbuilder.local");

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "notif-review");
        (await Api.PutMetaAsync(admin, eid, new { status = "UnderReview" })).EnsureSuccessStatusCode();

        Assert.True(Has(await InboxAsync(other), eid, "estimate.under-review"));
    }

    [Fact]
    public async Task New_approval_notifies_other_admins()
    {
        var admin = await fx.AdminClientAsync();
        var other = await NewAdminAsync(admin, "notif.approve.recipient@bidbuilder.local");

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "notif-approve");
        (await admin.PostAsJsonAsync($"/api/estimates/{eid}/approvals", new { note = (string?)null })).EnsureSuccessStatusCode();

        Assert.True(Has(await InboxAsync(other), eid, "estimate.approved"));
    }

    [Fact]
    public async Task Unread_count_mark_read_and_read_all()
    {
        var admin = await fx.AdminClientAsync();
        (await ClearApprovalRequirement(admin)).EnsureSuccessStatusCode();
        var me = await NewAdminAsync(admin, "notif.count.recipient@bidbuilder.local");

        Assert.Equal(0, await UnreadCountAsync(me));   // brand-new recipient

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "notif-count");
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();
        Assert.Equal(1, await UnreadCountAsync(me));

        // Mark that one read → back to zero.
        var inbox = await InboxAsync(me, unreadOnly: true);
        var nid = inbox.GetProperty("items").EnumerateArray()
            .First(x => x.GetProperty("entityKey").GetString() == eid.ToString()).GetProperty("id").GetInt64();
        (await me.PostAsync($"/api/notifications/{nid}/read", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, await UnreadCountAsync(me));

        // Another event, then read-all clears everything.
        var eid2 = await Api.NewEstimateAsync(admin, pid, "notif-count-2");
        (await Api.PutMetaAsync(admin, eid2, new { status = "Published" })).EnsureSuccessStatusCode();
        Assert.Equal(1, await UnreadCountAsync(me));
        var cleared = await (await me.PostAsync("/api/notifications/read-all", null)).Json();
        Assert.Equal(1, cleared.GetProperty("cleared").GetInt32());
        Assert.Equal(0, await UnreadCountAsync(me));
    }

    [Fact]
    public async Task Cannot_mark_anothers_notification_read()
    {
        var admin = await fx.AdminClientAsync();
        (await ClearApprovalRequirement(admin)).EnsureSuccessStatusCode();
        var owner = await NewAdminAsync(admin, "notif.owner@bidbuilder.local");
        var intruder = await NewAdminAsync(admin, "notif.intruder@bidbuilder.local");

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "notif-scope");
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();

        var ownerInbox = await InboxAsync(owner, unreadOnly: true);
        var nid = ownerInbox.GetProperty("items").EnumerateArray()
            .First(x => x.GetProperty("entityKey").GetString() == eid.ToString()).GetProperty("id").GetInt64();

        // Another recipient cannot reach into the owner's inbox (scoped → 404).
        var r = await intruder.PostAsync($"/api/notifications/{nid}/read", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // 27.4 — the list DTO exposes ReadAt: null while unread, an ISO timestamp once read.
    // The bell uses this to drive the Unread/Earlier split.
    [Fact]
    public async Task ReadAt_is_null_until_marked_then_set()
    {
        var admin = await fx.AdminClientAsync();
        (await ClearApprovalRequirement(admin)).EnsureSuccessStatusCode();
        var me = await NewAdminAsync(admin, "notif.readat.recipient@bidbuilder.local");

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "notif-readat");
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();

        var item = (await InboxAsync(me)).GetProperty("items").EnumerateArray()
            .First(x => x.GetProperty("entityKey").GetString() == eid.ToString());
        Assert.False(item.GetProperty("isRead").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("readAt").ValueKind);   // unread → null
        var nid = item.GetProperty("id").GetInt64();

        (await me.PostAsync($"/api/notifications/{nid}/read", null)).EnsureSuccessStatusCode();

        var read = (await InboxAsync(me)).GetProperty("items").EnumerateArray()
            .First(x => x.GetProperty("id").GetInt64() == nid);
        Assert.True(read.GetProperty("isRead").GetBoolean());
        Assert.Equal(JsonValueKind.String, read.GetProperty("readAt").ValueKind);  // read → ISO timestamp
    }
}
