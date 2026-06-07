using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

/// <summary>The signed-in user's digest preference plus whether the platform can actually
/// send (the toggle is moot when no SMTP transport is configured).</summary>
public record DigestPreferenceDto(string Frequency, DateTime? LastSentAt, bool EmailConfigured);

/// <summary>Set the caller's digest frequency. <c>Off | Daily | Weekly</c>.</summary>
public record DigestPreferenceInput(string Frequency);

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
                (pref?.Frequency ?? DigestFrequency.Off).ToString(), pref?.LastSentAt, email.Configured));
        });

        // Upsert the caller's frequency.
        grp.MapPut("/preferences", async (DigestPreferenceInput input, ClaimsPrincipal me, AppDbContext db, EmailService email) =>
        {
            if (!Enum.TryParse<DigestFrequency>(input.Frequency, ignoreCase: true, out var freq)
                || !Enum.IsDefined(freq))
                return Results.BadRequest(new { error = "Frequency must be Off, Daily, or Weekly." });

            var uid = me.Id();
            var pref = await db.NotificationDigestPreferences.FirstOrDefaultAsync(p => p.UserId == uid);
            if (pref is null)
            {
                pref = new NotificationDigestPreference { UserId = uid };   // TenantId auto-stamped on save
                db.NotificationDigestPreferences.Add(pref);
            }
            pref.Frequency = freq;
            pref.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new DigestPreferenceDto(pref.Frequency.ToString(), pref.LastSentAt, email.Configured));
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
