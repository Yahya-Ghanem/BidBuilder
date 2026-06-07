using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

/// <summary>The signed-in user's digest preference plus whether the platform can actually
/// send (the toggle is moot when no SMTP transport is configured).
/// 23.1 — Added <see cref="DayOfWeek"/> (only meaningful when Frequency=Weekly).</summary>
public record DigestPreferenceDto(string Frequency, string DayOfWeek, DateTime? LastSentAt, bool EmailConfigured);

/// <summary>Set the caller's digest frequency (<c>Off | Daily | Weekly</c>) and, optionally,
/// the day-of-week the weekly digest fires (any <see cref="System.DayOfWeek"/> name; defaults
/// to Monday when omitted). Ignored for Daily/Off.</summary>
public record DigestPreferenceInput(string Frequency, string? DayOfWeek);

/// <summary>Admin trigger: a userId sends that one user a digest now (preview/test);
/// omitted sends every opted-in user in the tenant immediately.</summary>
public record DigestSendNowInput(int? UserId);

/// <summary>
/// 22.1 — Per-user email-digest preferences (<c>/api/digests</c>). Any signed-in user
/// manages their own opt-in; the actual batching/sending is done by
/// <see cref="DigestSchedulerService"/>. A tenant admin can also trigger a send on demand.
/// </summary>
public static class DigestEndpoints
{
    public static void MapDigestEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/digests").RequireAuthorization();

        // The caller's own preference (absence of a row == Off).
        grp.MapGet("/preferences", async (ClaimsPrincipal me, AppDbContext db, EmailService email) =>
        {
            var uid = me.Id();
            var pref = await db.NotificationDigestPreferences.FirstOrDefaultAsync(p => p.UserId == uid);
            return Results.Ok(new DigestPreferenceDto(
                (pref?.Frequency ?? DigestFrequency.Off).ToString(),
                (pref?.DayOfWeek ?? System.DayOfWeek.Monday).ToString(),
                pref?.LastSentAt,
                email.Configured));
        });

        // Upsert the caller's frequency + day-of-week.
        grp.MapPut("/preferences", async (DigestPreferenceInput input, ClaimsPrincipal me, AppDbContext db, EmailService email) =>
        {
            if (!Enum.TryParse<DigestFrequency>(input.Frequency, ignoreCase: true, out var freq)
                || !Enum.IsDefined(freq))
                return Results.BadRequest(new { error = "Frequency must be Off, Daily, or Weekly." });

            var dow = System.DayOfWeek.Monday;
            if (!string.IsNullOrWhiteSpace(input.DayOfWeek))
            {
                if (!Enum.TryParse<System.DayOfWeek>(input.DayOfWeek, ignoreCase: true, out dow)
                    || !Enum.IsDefined(dow))
                    return Results.BadRequest(new { error = "DayOfWeek must be a valid weekday name (Sunday…Saturday)." });
            }

            var uid = me.Id();
            var pref = await db.NotificationDigestPreferences.FirstOrDefaultAsync(p => p.UserId == uid);
            if (pref is null)
            {
                pref = new NotificationDigestPreference { UserId = uid };   // TenantId auto-stamped on save
                db.NotificationDigestPreferences.Add(pref);
            }
            pref.Frequency = freq;
            pref.DayOfWeek = dow;
            pref.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new DigestPreferenceDto(
                pref.Frequency.ToString(), pref.DayOfWeek.ToString(), pref.LastSentAt, email.Configured));
        });

        // 23.1 — Preview the next digest body without sending. Any signed-in user, for themselves.
        grp.MapPost("/preview", async (ClaimsPrincipal me, ITenantContext tc, DigestService digest) =>
        {
            var p = await digest.PreviewForUserAsync(tc.TenantId, me.Id());
            return Results.Ok(p);
        });

        // 23.1 — Send a one-off test digest to the caller's own email. Bypasses watermark.
        grp.MapPost("/test", async (ClaimsPrincipal me, ITenantContext tc, DigestService digest, EmailService email) =>
        {
            if (!email.Configured) return Results.Ok(new { configured = false, sent = false });
            var ok = await digest.SendTestForUserAsync(tc.TenantId, me.Id());
            return Results.Ok(new { configured = true, sent = ok });
        });

        // Admin: send a digest now. With a userId → that one user (a preview that bypasses
        // schedule + opt-in); without → every opted-in user in this tenant immediately.
        grp.MapPost("/send-now", async (DigestSendNowInput input, ClaimsPrincipal me, ITenantContext tc, DigestService digest, EmailService email) =>
        {
            if (!me.IsAdmin())
                return Results.Json(new { error = "Only a tenant admin can trigger digests." }, statusCode: 403);
            if (!email.Configured)
                return Results.Ok(new { configured = false, sent = 0 });

            var now = DateTime.UtcNow;
            if (input.UserId is { } uid)
            {
                var ok = await digest.SendForUserAsync(tc.TenantId, uid, now);
                return Results.Ok(new { configured = true, sent = ok ? 1 : 0 });
            }
            var count = await digest.RunTenantDigestsAsync(tc.TenantId, now, onlyDue: false);
            return Results.Ok(new { configured = true, sent = count });
        });
    }
}
