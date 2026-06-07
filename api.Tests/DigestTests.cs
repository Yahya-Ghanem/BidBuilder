using System.Net;
using System.Net.Http.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 22.1 — Notification email digests. The scheduler's background timer is disabled in the
/// test host (Digests__Enabled=false), so these drive <see cref="DigestService"/> directly
/// with a controlled "now" and assert what WOULD be emailed via the
/// <see cref="CapturingEmailSender"/>.
///
/// Coverage: an opted-in user with pending notifications gets a batched email; Off sends
/// nothing; a not-yet-due user is skipped on the scheduled path but sent on admin "send now";
/// the watermark prevents a double send; the per-tenant toggle suppresses; tenants don't leak
/// into each other; and the preference endpoint validates + round-trips.
/// </summary>
[Collection("api")]
public class DigestTests(ApiFixture fx)
{
    static CapturingEmailSender Mail => ApiFixture.Emails;

    /// <summary>Seed a fresh user in <paramref name="slug"/> with a digest preference and
    /// <paramref name="notifications"/> notifications. Returns the user id + email + the unique
    /// notification title used (so assertions can find this user's digest in the shared capture).</summary>
    async Task<(int userId, string email, string title)> SeedAsync(
        string slug, DigestFrequency freq, DateTime? lastSentAt, int notifications, DateTime notifCreatedAt)
    {
        using var scope = await fx.CreateScope(slug);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tc = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        var tag = Guid.NewGuid().ToString("N");
        var email = $"digest.{tag}@bidbuilder.local";
        var user = new User
        {
            TenantId = tc.TenantId, Email = email, Name = "Digest User",
            Role = UserRole.TenantUser, IsActive = true, PasswordHash = "x",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();   // assigns user.Id

        db.NotificationDigestPreferences.Add(new NotificationDigestPreference
        {
            UserId = user.Id, Frequency = freq, LastSentAt = lastSentAt,
        });
        var title = $"Digest update {tag}";
        for (var i = 0; i < notifications; i++)
            db.Notifications.Add(new Notification
            {
                RecipientUserId = user.Id, Type = "test.digest", Title = title,
                Body = "Body text", Link = "/projects/1", CreatedAt = notifCreatedAt,
            });
        await db.SaveChangesAsync();
        return (user.Id, email, title);
    }

    /// <summary>Run the platform-wide due-digest pass (the scheduler's entry point).</summary>
    async Task<int> RunAllAsync(DateTime now)
    {
        using var scope = await fx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DigestService>().RunAllDueDigestsAsync(now);
    }

    [Fact]
    public async Task Daily_optin_with_notifications_sends_a_batched_digest()
    {
        var now = DateTime.UtcNow;
        var (_, email, title) = await SeedAsync("default", DigestFrequency.Daily, lastSentAt: null,
            notifications: 2, notifCreatedAt: now.AddMinutes(-5));

        var sent = await RunAllAsync(now);

        Assert.True(sent >= 1);
        var got = Mail.To(email);
        Assert.NotEmpty(got);
        Assert.Contains(got, m => m.Body.Contains(title));
        Assert.Contains(got, m => m.Subject.Contains("daily digest"));
    }

    [Fact]
    public async Task Off_preference_sends_nothing()
    {
        var now = DateTime.UtcNow;
        var (_, email, _) = await SeedAsync("default", DigestFrequency.Off, lastSentAt: null,
            notifications: 2, notifCreatedAt: now.AddMinutes(-5));

        await RunAllAsync(now);

        Assert.Empty(Mail.To(email));
    }

    [Fact]
    public async Task A_not_yet_due_user_is_skipped_on_the_scheduled_pass()
    {
        var now = DateTime.UtcNow;
        // Sent an hour ago → a Daily digest isn't due (needs ~24h).
        var (_, email, _) = await SeedAsync("default", DigestFrequency.Daily, lastSentAt: now.AddHours(-1),
            notifications: 2, notifCreatedAt: now.AddMinutes(-5));

        await RunAllAsync(now);

        Assert.Empty(Mail.To(email));
    }

    [Fact]
    public async Task The_watermark_prevents_a_double_send()
    {
        var now = DateTime.UtcNow;
        var (_, email, _) = await SeedAsync("default", DigestFrequency.Daily, lastSentAt: null,
            notifications: 1, notifCreatedAt: now.AddMinutes(-5));

        await RunAllAsync(now);
        await RunAllAsync(DateTime.UtcNow);   // a second pass finds nothing newer

        Assert.Single(Mail.To(email));
    }

    [Fact]
    public async Task Send_now_for_a_user_bypasses_the_schedule()
    {
        var admin = await fx.AdminClientAsync();
        var now = DateTime.UtcNow;
        // Not due on the schedule, but the admin can force it.
        var (uid, email, title) = await SeedAsync("default", DigestFrequency.Daily, lastSentAt: now.AddHours(-1),
            notifications: 1, notifCreatedAt: now.AddMinutes(-5));

        var resp = await admin.PostAsJsonAsync("/api/digests/send-now", new { userId = uid });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Json();

        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Equal(1, body.GetProperty("sent").GetInt32());
        Assert.Contains(Mail.To(email), m => m.Body.Contains(title));
    }

    [Fact]
    public async Task Tenant_toggle_off_suppresses_the_digest()
    {
        // Isolate on a dedicated tenant so toggling its email setting can't perturb others.
        await fx.SecondTenantAdminClientAsync("tenant3", "admin@tenant3.local");
        using (var scope = await fx.CreateScope("tenant3"))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var s = await db.TenantSettings.FirstOrDefaultAsync();
            if (s is null) { s = new TenantSettings(); db.TenantSettings.Add(s); }
            s.NotificationEmailsEnabled = false;
            await db.SaveChangesAsync();
        }

        var now = DateTime.UtcNow;
        var (_, email, _) = await SeedAsync("tenant3", DigestFrequency.Daily, lastSentAt: null,
            notifications: 2, notifCreatedAt: now.AddMinutes(-5));

        using (var scope = await fx.CreateScope("tenant3"))
        {
            var digest = scope.ServiceProvider.GetRequiredService<DigestService>();
            var tc = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var sent = await digest.RunTenantDigestsAsync(tc.TenantId, now, onlyDue: false);
            Assert.Equal(0, sent);
        }
        Assert.Empty(Mail.To(email));
    }

    [Fact]
    public async Task Digests_do_not_leak_across_tenants()
    {
        await fx.SecondTenantAdminClientAsync();   // tenant2 (notification emails default ON)
        var now = DateTime.UtcNow;
        var (_, emailA, titleA) = await SeedAsync("default", DigestFrequency.Daily, null, 1, now.AddMinutes(-5));
        var (_, emailB, titleB) = await SeedAsync("tenant2", DigestFrequency.Daily, null, 1, now.AddMinutes(-5));

        await RunAllAsync(now);

        var gotA = Mail.To(emailA);
        var gotB = Mail.To(emailB);
        Assert.Contains(gotA, m => m.Body.Contains(titleA));
        Assert.Contains(gotB, m => m.Body.Contains(titleB));
        Assert.DoesNotContain(gotA, m => m.Body.Contains(titleB));   // tenant A never sees tenant B's content
    }

    [Fact]
    public async Task Preference_endpoint_validates_and_round_trips()
    {
        var admin = await fx.AdminClientAsync();
        try
        {
            (await admin.PutAsJsonAsync("/api/digests/preferences", new { frequency = "Weekly" })).EnsureSuccessStatusCode();
            var get = await (await admin.GetAsync("/api/digests/preferences")).Json();
            Assert.Equal("Weekly", get.GetProperty("frequency").GetString());
            Assert.True(get.GetProperty("emailConfigured").GetBoolean());

            var bad = await admin.PutAsJsonAsync("/api/digests/preferences", new { frequency = "Hourly" });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally
        {
            // Leave the shared admin opted-out so a later platform pass doesn't email them.
            (await admin.PutAsJsonAsync("/api/digests/preferences", new { frequency = "Off" })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Send_now_is_admin_only()
    {
        var admin = await fx.AdminClientAsync();
        var email = $"digest.nonadmin.{Guid.NewGuid():N}@bidbuilder.local";
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Plain", email, password = "Pw@123456", role = "TenantUser", groupIds = Array.Empty<int>() })).Json();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");

        var resp = await user.PostAsJsonAsync("/api/digests/send-now", new { });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
