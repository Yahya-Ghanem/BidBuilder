using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

/// <summary>A webhook subscription as listed (the secret is never echoed back).</summary>
public record WebhookDto(int Id, string Url, string Events, bool IsActive, DateTime CreatedAt,
    string? LastStatus, DateTime? LastAttemptAt, int FailureCount);
/// <summary>Create/update payload. Events null/empty ⇒ "*" (all events).</summary>
public record WebhookInput(string? Url, string[]? Events, bool? IsActive);

/// <summary>
/// 20.9 — Tenant webhook management (TenantAdmin only). Subscriptions are
/// tenant-scoped by query filter; the signing secret is shown exactly once (on
/// create) and never returned again, so a leaked list response can't be replayed.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/webhooks").RequireAuthorization();

        // GET list — never includes the secret.
        grp.MapGet("/", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var items = await db.WebhookSubscriptions
                .OrderBy(w => w.Id)
                .Select(w => new WebhookDto(w.Id, w.Url, w.Events, w.IsActive, w.CreatedAt, w.LastStatus, w.LastAttemptAt, w.FailureCount))
                .ToListAsync();
            return Results.Ok(items);
        });

        // The catalog of subscribable event types (drives the UI checkboxes).
        grp.MapGet("/events", (ClaimsPrincipal me) =>
            me.IsAdmin() ? Results.Ok(WebhookEvents.Subscribable) : Forbid());

        // POST create — generates the signing secret and returns it ONCE.
        grp.MapPost("/", async (WebhookInput input, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var (events, urlErr) = Validate(input);
            if (urlErr is not null) return urlErr;

            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var w = new WebhookSubscription
            {
                Url = input.Url!.Trim(), Secret = secret, Events = events,
                IsActive = input.IsActive ?? true,
            };
            db.WebhookSubscriptions.Add(w);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "webhook.create", "Webhook", w.Id.ToString(), $"{w.Url} [{w.Events}]");
            // Secret returned here and ONLY here.
            return Results.Created($"/api/webhooks/{w.Id}", new
            {
                w.Id, w.Url, w.Events, w.IsActive, w.CreatedAt, w.LastStatus, w.LastAttemptAt, w.FailureCount,
                secret = w.Secret,
            });
        });

        // PUT update — url / events / active (each optional).
        grp.MapPut("/{id:int}", async (int id, WebhookInput input, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var w = await db.WebhookSubscriptions.FirstOrDefaultAsync(x => x.Id == id);
            if (w is null) return Results.NotFound(new { error = "Webhook not found" });

            if (input.Url is not null)
            {
                if (!IsValidUrl(input.Url)) return Bad("URL must be an absolute http(s) URL.");
                w.Url = input.Url.Trim();
            }
            if (input.Events is not null)
            {
                var (events, err) = NormalizeEvents(input.Events);
                if (err is not null) return err;
                w.Events = events;
            }
            if (input.IsActive is { } active) w.IsActive = active;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "webhook.update", "Webhook", w.Id.ToString(), $"{w.Url} [{w.Events}] active={w.IsActive}");
            return Results.Ok(new WebhookDto(w.Id, w.Url, w.Events, w.IsActive, w.CreatedAt, w.LastStatus, w.LastAttemptAt, w.FailureCount));
        });

        grp.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db, AuditService audit) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var w = await db.WebhookSubscriptions.FirstOrDefaultAsync(x => x.Id == id);
            if (w is null) return Results.NotFound(new { error = "Webhook not found" });
            db.WebhookSubscriptions.Remove(w);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "webhook.delete", "Webhook", id.ToString(), w.Url);
            return Results.NoContent();
        });

        // POST ping — deliver a test event to just this subscription and report the outcome.
        grp.MapPost("/{id:int}/ping", async (int id, ClaimsPrincipal me, AppDbContext db, WebhookDispatcher dispatcher) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var w = await db.WebhookSubscriptions.FirstOrDefaultAsync(x => x.Id == id);
            if (w is null) return Results.NotFound(new { error = "Webhook not found" });
            var r = await dispatcher.DeliverAsync(w, WebhookEvents.Ping, new { message = "Ping from BidBuilder", webhookId = w.Id });
            return Results.Ok(new { ok = r.Ok, status = r.Status });
        });
    }

    static (string Events, IResult? Error) NormalizeEvents(string[]? events)
    {
        if (events is null || events.Length == 0) return ("*", null);
        if (events.Contains("*")) return ("*", null);
        var bad = events.Where(e => !WebhookEvents.Subscribable.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        if (bad.Count > 0) return ("", Bad($"Unknown event(s): {string.Join(", ", bad)}"));
        return (string.Join(",", events.Select(e => e.Trim()).Distinct()), null);
    }

    static (string Events, IResult? Error) Validate(WebhookInput input)
    {
        if (!IsValidUrl(input.Url)) return ("", Bad("URL must be an absolute http(s) URL."));
        return NormalizeEvents(input.Events);
    }

    static bool IsValidUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    static IResult Bad(string msg) => Results.BadRequest(new { error = msg });
    static IResult Forbid() => Results.Json(new { error = "Only a tenant admin can manage webhooks" }, statusCode: 403);
}
