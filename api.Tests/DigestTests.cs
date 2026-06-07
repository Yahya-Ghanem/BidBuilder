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
        string slug, DigestFrequency freq, DateTime? lastSentAt, int notifications, DateTime notifCreatedAt,
        DayOfWeek dayOfWeek = DayOfWeek.Monday)
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
            UserId = user.Id, Frequency = freq, LastSentAt = lastSentAt, DayOfWeek = dayOfWeek,
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

    // ── 23.1 weekly-day cadence + preview + test send ─────────────────────────

    // 2026-01-05 is a Monday; 2026-01-06 a Tuesday (used to drive DayOfWeek without
    // depending on the wall clock).
    static readonly DateTime MondayNoon  = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
    static readonly DateTime TuesdayNoon = new(2026, 1, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Weekly_digest_fires_only_on_the_chosen_day_of_week()
    {
        // A "Weekly on Monday" preference is due on Monday but skipped on Tuesday, even
        // though plenty of time has elapsed in both cases.
        var (_, emailMon, titleMon) = await SeedAsync("default", DigestFrequency.Weekly,
            lastSentAt: null, notifications: 1, notifCreatedAt: MondayNoon.AddMinutes(-10),
            dayOfWeek: DayOfWeek.Monday);
        var (_, emailTue, _) = await SeedAsync("default", DigestFrequency.Weekly,
            lastSentAt: null, notifications: 1, notifCreatedAt: MondayNoon.AddMinutes(-10),
            dayOfWeek: DayOfWeek.Tuesday);

        await RunAllAsync(MondayNoon);

        Assert.Contains(Mail.To(emailMon), m => m.Body.Contains(titleMon));
        Assert.Empty(Mail.To(emailTue));   // Tuesday-scheduled user not due yet on Monday
    }

    [Fact]
    public async Task Weekly_digest_skipped_within_six_days_even_on_the_right_day()
    {
        // Sent two days ago on the same weekday (impossible in practice but a useful guard
        // against an off-by-one in the elapsed check). Even with DayOfWeek matching, the
        // 6-day floor blocks a re-send.
        var twoDaysBefore = MondayNoon.AddDays(-2);
        var (_, email, _) = await SeedAsync("default", DigestFrequency.Weekly,
            lastSentAt: twoDaysBefore, notifications: 1, notifCreatedAt: MondayNoon.AddMinutes(-5),
            dayOfWeek: DayOfWeek.Monday);

        await RunAllAsync(MondayNoon);

        Assert.Empty(Mail.To(email));
    }

    [Fact]
    public async Task Preview_returns_pending_notification_count_and_body()
    {
        var admin = await fx.AdminClientAsync();
        // Opt the shared admin into Daily for the preview path; restore Off in finally so a
        // later platform pass doesn't email them.
        (await admin.PutAsJsonAsync("/api/digests/preferences", new { frequency = "Daily" })).EnsureSuccessStatusCode();
        try
        {
            // Drop a notification for the admin so there's something to preview.
            var tag = Guid.NewGuid().ToString("N");
            var title = $"Preview update {tag}";
            using (var scope = await fx.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var tc = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                var adminUid = await db.Users.IgnoreQueryFilters()
                    .Where(u => u.TenantId == tc.TenantId && u.Email == "admin@bidbuilder.local")
                    .Select(u => u.Id).FirstAsync();
                db.Notifications.Add(new Notification
                {
                    RecipientUserId = adminUid, Type = "test.preview", Title = title, Body = "Hi",
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            var resp = await admin.PostAsJsonAsync("/api/digests/preview", new { });
            resp.EnsureSuccessStatusCode();
            var body = await resp.Json();

            Assert.True(body.GetProperty("itemCount").GetInt32() >= 1);
            Assert.Contains(title, body.GetProperty("body").GetString() ?? "");
            Assert.Contains("digest", body.GetProperty("subject").GetString() ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/digests/preferences", new { frequency = "Off" })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Preview_does_not_advance_the_watermark()
    {
        var admin = await fx.AdminClientAsync();
        (await admin.PutAsJsonAsync("/api/digests/preferences", new { frequency = "Daily" })).EnsureSuccessStatusCode();
        try
        {
            var before = (await (await admin.GetAsync("/api/digests/preferences")).Json())
                .GetProperty("lastSentAt");
            (await admin.PostAsJsonAsync("/api/digests/preview", new { })).EnsureSuccessStatusCode();
            var after = (await (await admin.GetAsync("/api/digests/preferences")).Json())
                .GetProperty("lastSentAt");

            // Both should serialize to the same JSON (null or identical timestamp).
            Assert.Equal(before.GetRawText(), after.GetRawText());
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/digests/preferences", new { frequency = "Off" })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Send_test_emails_caller_and_does_not_advance_watermark()
    {
        var admin = await fx.AdminClientAsync();
        (await admin.PutAsJsonAsync("/api/digests/preferences", new { frequency = "Daily" })).EnsureSuccessStatusCode();
        try
        {
            var before = (await (await admin.GetAsync("/api/digests/preferences")).Json())
                .GetProperty("lastSentAt");

            var resp = await admin.PostAsJsonAsync("/api/digests/test", new { });
            resp.EnsureSuccessStatusCode();
            var body = await resp.Json();
            Assert.True(body.GetProperty("configured").GetBoolean());
            Assert.True(body.GetProperty("sent").GetBoolean());

            // Watermark unchanged — a test send must NOT count as the next scheduled digest.
            var after = (await (await admin.GetAsync("/api/digests/preferences")).Json())
                .GetProperty("lastSentAt");
            Assert.Equal(before.GetRawText(), after.GetRawText());

            // The captured email is addressed to the admin and carries the "[TEST]" marker.
            var got = Mail.To("admin@bidbuilder.local");
            Assert.Contains(got, m => m.Subject.StartsWith("[TEST]") || m.Subject.Contains("test digest", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/digests/preferences", new { frequency = "Off" })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Preferences_endpoint_round_trips_day_of_week_and_rejects_garbage()
    {
        var admin = await fx.AdminClientAsync();
        try
        {
            (await admin.PutAsJsonAsync("/api/digests/preferences",
                new { frequency = "Weekly", dayOfWeek = "Friday" })).EnsureSuccessStatusCode();
            var get = await (await admin.GetAsync("/api/digests/preferences")).Json();
            Assert.Equal("Weekly", get.GetProperty("frequency").GetString());
            Assert.Equal("Friday", get.GetProperty("dayOfWeek").GetString());

            var bad = await admin.PutAsJsonAsync("/api/digests/preferences",
                new { frequency = "Weekly", dayOfWeek = "Funday" });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/digests/preferences",
                new { frequency = "Off", dayOfWeek = "Monday" })).EnsureSuccessStatusCode();
        }
    }
}
