using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 21.1 — Email delivery. The test host swaps the real SMTP transport for a
/// <see cref="CapturingEmailSender"/> (reports configured, records messages), so
/// these assert what WOULD be emailed without a mail server.
///
/// Coverage:
///   • Publishing an estimate emails the notified audience (the same fan-out as the
///     in-app bell), to a unique recipient address so the capture is isolated.
///   • The "send test email" endpoint reaches the calling admin.
///   • A subcontractor RFQ with an address emails the portal link; without one,
///     nothing is sent.
///   • The per-tenant toggle suppresses sends even when the transport is configured.
/// </summary>
[Collection("api")]
public class EmailDeliveryTests(ApiFixture fx)
{
    static CapturingEmailSender Mail => ApiFixture.Emails;

    static Task<HttpResponseMessage> ClearApprovalRequirement(HttpClient admin) =>
        admin.PutAsJsonAsync("/api/settings/", new
        {
            timezone = "UTC", baseCurrency = "AED",
            defaultOverheadPct = 0m, defaultProfitPct = 0m,
            defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            requiredApprovalsToPublish = 0,
        });

    async Task<HttpClient> NewAdminAsync(HttpClient admin, string email)
    {
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Mail Recipient", email, password = "Pw@123456", role = "TenantAdmin", groupIds = Array.Empty<int>() })).Json();
        return await fx.AuthedClientAsync(email, "Pw@123456");
    }

    [Fact]
    public async Task Publishing_an_estimate_emails_the_notified_admins()
    {
        var admin = await fx.AdminClientAsync();
        (await ClearApprovalRequirement(admin)).EnsureSuccessStatusCode();
        var recipientEmail = $"email.publish.{Guid.NewGuid():N}@bidbuilder.local";
        await NewAdminAsync(admin, recipientEmail);   // a fresh admin = a notification recipient

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "email-publish");
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();

        var got = Mail.To(recipientEmail);
        Assert.NotEmpty(got);
        Assert.Contains(got, m => m.Subject.StartsWith("Estimate published"));
        // The body carries the in-app link to the project.
        Assert.Contains(got, m => m.Body.Contains($"/projects/{pid}"));
    }

    [Fact]
    public async Task Test_email_endpoint_sends_to_the_calling_admin()
    {
        var admin = await fx.AdminClientAsync();
        var resp = await admin.PostAsync("/api/settings/email/test", null);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Json();
        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.True(body.GetProperty("sent").GetBoolean());

        Assert.Contains(Mail.To("admin@bidbuilder.local"), m => m.Subject == "BidBuilder test email");
    }

    [Fact]
    public async Task Test_email_endpoint_is_admin_only()
    {
        // A view-only estimator (no admin) must not be able to send a test email.
        var admin = await fx.AdminClientAsync();
        var email = $"email.nonadmin.{Guid.NewGuid():N}@bidbuilder.local";
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Plain", email, password = "Pw@123456", role = "TenantUser", groupIds = Array.Empty<int>() })).Json();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");

        var resp = await user.PostAsync("/api/settings/email/test", null);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Subcontractor_invite_emails_the_portal_link()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var to = $"sub.{Guid.NewGuid():N}@example.test";

        var created = await (await admin.PostAsJsonAsync("/api/subcontractor-quotes/", new
        {
            projectId = pid, trade = "Waterproofing", scope = "Roof membrane to block A",
            contractorName = "Acme Subs", contractorEmail = to, validDays = 14,
        })).Json();
        var token = created.GetProperty("token").GetString()!;

        var got = Mail.To(to);
        Assert.Single(got);
        Assert.Contains("/portal/", got[0].Body);
        Assert.Contains(token, got[0].Body);
        Assert.Contains("Waterproofing", got[0].Subject);
    }

    [Fact]
    public async Task Subcontractor_create_without_email_sends_nothing()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);

        var created = await (await admin.PostAsJsonAsync("/api/subcontractor-quotes/", new
        {
            projectId = pid, trade = "Painting", scope = "Internal walls",
            contractorName = "No Email Co", validDays = 7,
        })).Json();
        var token = created.GetProperty("token").GetString()!;

        // No address was supplied → nothing should reference this RFQ's token.
        Assert.DoesNotContain(Mail.All(), m => m.Body.Contains(token));
    }

    [Fact]
    public async Task Tenant_toggle_off_suppresses_emails()
    {
        using var scope = await fx.CreateScope();   // default tenant resolved
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<EmailService>();

        var s = await db.TenantSettings.FirstOrDefaultAsync();
        var created = false;
        if (s is null) { s = new TenantSettings(); db.TenantSettings.Add(s); created = true; }
        var prev = s.NotificationEmailsEnabled;
        s.NotificationEmailsEnabled = false;
        await db.SaveChangesAsync();
        try
        {
            var u = $"toggle.{Guid.NewGuid():N}@example.test";
            var sent = await email.SendAsync(u, "X", "Should not send", "body");   // respects toggle
            Assert.False(sent);
            Assert.Empty(Mail.To(u));
        }
        finally
        {
            if (created) db.TenantSettings.Remove(s);
            else s.NotificationEmailsEnabled = prev;
            await db.SaveChangesAsync();
        }
    }
}
