using System.Text;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Services;

/// <summary>
/// 22.1 — Assembles and sends per-user notification digests: a batched plain-text email
/// of the in-app notifications a user accumulated since their last digest.
///
/// Runs in two contexts and must behave identically in both:
///   • the timer-driven <see cref="DigestSchedulerService"/>, which owns NO request and
///     therefore NO resolved tenant; and
///   • the admin "send now" endpoint, where a tenant IS resolved.
/// To be context-agnostic every read bypasses the tenant query filter and scopes by an
/// explicit <c>TenantId</c>, and sends go straight through the <see cref="IEmailSender"/>
/// transport (the per-tenant "notification emails" toggle is checked here, once per tenant).
///
/// Best-effort and failure-isolating, like <see cref="NotificationService"/>: one user's
/// failure is logged and never aborts the rest of the batch.
/// </summary>
public class DigestService(
    AppDbContext db,
    IEmailSender sender,
    EmailService email,
    ILogger<DigestService> logger)
{
    // Per-digest cap so a long-dormant inbox can't produce a giant email.
    private const int MaxItemsPerDigest = 50;

    /// <summary>Send due digests for every tenant (the scheduler's entry point).
    /// Returns the number of digests actually emailed.</summary>
    public async Task<int> RunAllDueDigestsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        if (!sender.IsConfigured) return 0;
        // Tenants is not tenant-filtered; enumerate every workspace.
        var tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        var total = 0;
        foreach (var tid in tenantIds)
            total += await RunTenantDigestsAsync(tid, nowUtc, onlyDue: true, ct);
        return total;
    }

    /// <summary>Send digests for one tenant's opted-in users. With <paramref name="onlyDue"/>
    /// true (the scheduled path) a user is skipped until their frequency interval has elapsed;
    /// false (the admin "send now, everyone" path) forces a send to every opted-in user with
    /// pending notifications. Honors the tenant's notification-email toggle. Returns the count sent.</summary>
    public async Task<int> RunTenantDigestsAsync(Guid tenantId, DateTime nowUtc, bool onlyDue, CancellationToken ct = default)
    {
        if (!sender.IsConfigured) return 0;
        if (!await TenantEmailsEnabledAsync(tenantId, ct)) return 0;

        var prefs = await db.NotificationDigestPreferences.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.Frequency != DigestFrequency.Off)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var p in prefs)
        {
            if (onlyDue && !IsDue(p, nowUtc)) continue;
            try
            {
                if (await SendDigestAsync(p, nowUtc, ct)) sent++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Digest send failed for user {UserId} in tenant {TenantId}", p.UserId, tenantId);
            }
        }
        return sent;
    }

    /// <summary>Send a digest to a single user immediately (admin "send now" for one user).
    /// Respects the tenant toggle + SMTP config but bypasses the schedule and the opt-in
    /// requirement, so an admin can preview/test even for a user who hasn't opted in. When the
    /// user has a preference row its watermark is advanced; an ad-hoc preview (no row) is not
    /// persisted. Returns whether an email was sent.</summary>
    public async Task<bool> SendForUserAsync(Guid tenantId, int userId, DateTime nowUtc, CancellationToken ct = default)
    {
        if (!sender.IsConfigured) return false;
        if (!await TenantEmailsEnabledAsync(tenantId, ct)) return false;

        var pref = await db.NotificationDigestPreferences.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == userId, ct);
        // Synthesize a transient preference if the user never opted in (preview semantics):
        // include everything (LastSentAt null) and don't persist a watermark.
        pref ??= new NotificationDigestPreference { TenantId = tenantId, UserId = userId, Frequency = DigestFrequency.Daily };
        return await SendDigestAsync(pref, nowUtc, ct, persistWatermark: pref.Id != 0);
    }

    /// <summary>23.1 — Build the digest body for a user WITHOUT sending or touching the
    /// watermark. Used by the "Preview" button so a user can see exactly what the next
    /// digest would contain. Always materializes from the user's current cursor; returns
    /// a zero-item preview when there's nothing to send.</summary>
    public async Task<DigestPreview> PreviewForUserAsync(Guid tenantId, int userId, CancellationToken ct = default)
    {
        var pref = await db.NotificationDigestPreferences.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == userId, ct);
        // Transient preference for the never-opted-in case mirrors SendForUserAsync.
        pref ??= new NotificationDigestPreference { TenantId = tenantId, UserId = userId, Frequency = DigestFrequency.Daily };

        var since = pref.LastSentAt ?? DateTime.MinValue;
        var items = await db.Notifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == pref.TenantId && n.RecipientUserId == pref.UserId && n.CreatedAt > since)
            .OrderByDescending(n => n.CreatedAt)
            .Take(MaxItemsPerDigest)
            .ToListAsync(ct);

        if (items.Count == 0)
            return new DigestPreview(pref.Frequency.ToString(), 0,
                $"BidBuilder {(pref.Frequency == DigestFrequency.Weekly ? "weekly" : "daily")} digest — nothing new",
                "You have no new notifications since your last digest.");

        var (subject, body) = Compose(items, pref.Frequency);
        return new DigestPreview(pref.Frequency.ToString(), items.Count, subject, body);
    }

    /// <summary>23.1 — Send a one-off "test" digest to a user's own address regardless of
    /// frequency / due-state / opt-in. Includes at least the most recent notification so
    /// the body is never literally empty; falls back to a synthetic line when there are
    /// genuinely no in-app notifications yet. Does NOT advance the watermark — this is a
    /// test, not the real next digest. Returns whether the email was sent.</summary>
    public async Task<bool> SendTestForUserAsync(Guid tenantId, int userId, CancellationToken ct = default)
    {
        if (!sender.IsConfigured) return false;
        if (!await TenantEmailsEnabledAsync(tenantId, ct)) return false;

        var recipient = await db.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && u.Id == userId && u.IsActive && u.Email != "")
            .Select(u => new { u.Email, u.Name })
            .FirstOrDefaultAsync(ct);
        if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email)) return false;

        // Use the most recent notifications regardless of watermark, capped to MaxItemsPerDigest.
        var items = await db.Notifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId && n.RecipientUserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(MaxItemsPerDigest)
            .ToListAsync(ct);

        string subject, body;
        if (items.Count == 0)
        {
            subject = "BidBuilder test digest";
            body = "This is a test digest from BidBuilder. You currently have no in-app notifications — when you do, they'll appear here.\n\nReceived because you clicked \"Send test\" in your digest settings.";
        }
        else
        {
            (subject, body) = Compose(items, DigestFrequency.Daily);
            subject = "[TEST] " + subject;
            body = "This is a TEST digest — your watermark has NOT advanced.\n\n" + body;
        }
        return await sender.SendAsync(new EmailMessage(recipient.Email, recipient.Name, subject, body), ct);
    }

    // ── internals ────────────────────────────────────────────────────────────────
    private async Task<bool> SendDigestAsync(NotificationDigestPreference pref, DateTime nowUtc,
        CancellationToken ct, bool persistWatermark = true)
    {
        var recipient = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == pref.UserId && u.IsActive && u.Email != "")
            .Select(u => new { u.Email, u.Name })
            .FirstOrDefaultAsync(ct);
        if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email)) return false;

        var since = pref.LastSentAt ?? DateTime.MinValue;
        var items = await db.Notifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == pref.TenantId && n.RecipientUserId == pref.UserId && n.CreatedAt > since)
            .OrderByDescending(n => n.CreatedAt)
            .Take(MaxItemsPerDigest)
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            // Nothing new — skip the empty email, but still advance the watermark so the
            // next due-run starts cleanly rather than re-evaluating the same stale window.
            if (persistWatermark) await StampSentAsync(pref, nowUtc, ct);
            return false;
        }

        var (subject, body) = Compose(items, pref.Frequency);
        var ok = await sender.SendAsync(new EmailMessage(recipient.Email, recipient.Name, subject, body), ct);
        if (persistWatermark) await StampSentAsync(pref, nowUtc, ct);
        return ok;
    }

    /// <summary>Advance the watermark with a single filter-bypassing UPDATE — this is the
    /// atomic double-send guard (a concurrent run finds nothing newer to send).</summary>
    private async Task StampSentAsync(NotificationDigestPreference pref, DateTime nowUtc, CancellationToken ct)
    {
        await db.NotificationDigestPreferences.IgnoreQueryFilters()
            .Where(p => p.Id == pref.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.LastSentAt, nowUtc)
                .SetProperty(p => p.UpdatedAt, nowUtc), ct);
        pref.LastSentAt = nowUtc;
    }

    private (string Subject, string Body) Compose(List<Notification> items, DigestFrequency freq)
    {
        var period = freq == DigestFrequency.Weekly ? "weekly" : "daily";
        var subject = $"BidBuilder {period} digest — {items.Count} update{(items.Count == 1 ? "" : "s")}";

        var sb = new StringBuilder();
        sb.AppendLine($"You have {items.Count} new notification{(items.Count == 1 ? "" : "s")} in your BidBuilder workspace:");
        sb.AppendLine();
        foreach (var n in items)
        {
            sb.AppendLine($"• {n.Title}");
            if (!string.IsNullOrWhiteSpace(n.Body)) sb.AppendLine($"  {n.Body}");
            var link = email.AbsoluteLink(n.Link);
            if (!string.IsNullOrWhiteSpace(link)) sb.AppendLine($"  {link}");
            sb.AppendLine();
        }
        sb.AppendLine("You're receiving this because you enabled digest emails. Change the frequency in your BidBuilder settings.");
        return (subject, sb.ToString().TrimEnd());
    }

    /// <summary>Has enough time elapsed for this user's next digest? A small tolerance is
    /// subtracted so ordinary timer jitter doesn't push a "daily" digest to ~25h.
    /// 23.1 — Weekly additionally honors <see cref="NotificationDigestPreference.DayOfWeek"/>:
    /// even after 7 days have elapsed, a "Monday weekly" digest only fires on Monday. The
    /// first send (LastSentAt == null) waits for the chosen day too so the very first email
    /// matches the user's stated preference instead of going out immediately.</summary>
    private static bool IsDue(NotificationDigestPreference p, DateTime nowUtc) => p.Frequency switch
    {
        DigestFrequency.Daily  => p.LastSentAt is null
            || (nowUtc - p.LastSentAt.Value) >= TimeSpan.FromHours(24) - TimeSpan.FromMinutes(30),
        DigestFrequency.Weekly => nowUtc.DayOfWeek == p.DayOfWeek
            && (p.LastSentAt is null
                || (nowUtc - p.LastSentAt.Value) >= TimeSpan.FromDays(6)),
        _ => false,
    };

    private async Task<bool> TenantEmailsEnabledAsync(Guid tenantId, CancellationToken ct)
    {
        var enabled = await db.TenantSettings.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .Select(s => (bool?)s.NotificationEmailsEnabled)
            .FirstOrDefaultAsync(ct);
        return enabled ?? true;   // default ON when the tenant has no settings row yet
    }
}

/// <summary>23.1 — Lightweight DTO returned by <see cref="DigestService.PreviewForUserAsync"/>.
/// <paramref name="Frequency"/> is the user's current frequency string; <paramref name="ItemCount"/>
/// is the number of notifications that would be included (zero means "nothing new since last digest").</summary>
public record DigestPreview(string Frequency, int ItemCount, string Subject, string Body);
