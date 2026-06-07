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
    /// subtracted so ordinary timer jitter doesn't push a "daily" digest to ~25h.</summary>
    private static bool IsDue(NotificationDigestPreference p, DateTime nowUtc)
    {
        if (p.LastSentAt is null) return true;
        var elapsed = nowUtc - p.LastSentAt.Value;
        return p.Frequency switch
        {
            DigestFrequency.Daily  => elapsed >= TimeSpan.FromHours(24) - TimeSpan.FromMinutes(30),
            DigestFrequency.Weekly => elapsed >= TimeSpan.FromDays(7)   - TimeSpan.FromHours(1),
            _                      => false,
        };
    }

    private async Task<bool> TenantEmailsEnabledAsync(Guid tenantId, CancellationToken ct)
    {
        var enabled = await db.TenantSettings.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .Select(s => (bool?)s.NotificationEmailsEnabled)
            .FirstOrDefaultAsync(ct);
        return enabled ?? true;   // default ON when the tenant has no settings row yet
    }
}
