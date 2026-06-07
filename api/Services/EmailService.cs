using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using BidBuilder.Api.Data;

namespace BidBuilder.Api.Services;

/// <summary>
/// 21.1 — The application-level email layer that sits on top of the raw
/// <see cref="IEmailSender"/> transport. It owns the three concerns the transport
/// shouldn't know about: resolving recipient addresses from user ids, honoring the
/// per-tenant "send notification emails" toggle, and turning the relative in-app
/// links carried by notifications into absolute URLs.
///
/// Like <see cref="NotificationService"/> / <see cref="AuditService"/>, every send
/// is best-effort and failure-swallowing — email must never break the action that
/// prompted it. Scoped (it uses the request <see cref="AppDbContext"/>).
/// </summary>
public class EmailService(
    AppDbContext db,
    IEmailSender sender,
    IOptions<EmailOptions> opt,
    IConfiguration config,
    ILogger<EmailService> logger)
{
    private readonly EmailOptions _o = opt.Value;

    /// <summary>Cheap gate so callers can skip recipient resolution entirely.</summary>
    public bool Configured => sender.IsConfigured;

    /// <summary>Email the same audience an in-app notification was just written for.
    /// Resolves active users' addresses, respects the tenant toggle, and appends the
    /// absolute link. A no-op when email is off, the tenant disabled it, or nobody
    /// has an address.</summary>
    public async Task NotifyByEmailAsync(IEnumerable<int> recipientUserIds, string subject,
        string? body = null, string? link = null)
    {
        try
        {
            if (!sender.IsConfigured) return;
            if (!await TenantEmailsEnabledAsync()) return;

            var ids = recipientUserIds.Where(id => id > 0).Distinct().ToList();
            if (ids.Count == 0) return;

            var recipients = await db.Users
                .Where(u => ids.Contains(u.Id) && u.IsActive && u.Email != null && u.Email != "")
                .Select(u => new { u.Email, u.Name })
                .ToListAsync();
            if (recipients.Count == 0) return;

            var text = ComposeBody(body, link);
            foreach (var r in recipients)
                await sender.SendAsync(new EmailMessage(r.Email!, r.Name, subject, text));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification email fan-out failed: {Subject}", subject);
        }
    }

    /// <summary>Send a single email to an explicit external address (e.g. a
    /// subcontractor invite). Honors the transport + tenant toggle. Returns whether
    /// it was actually sent.</summary>
    public async Task<bool> SendAsync(string toEmail, string? toName, string subject, string body,
        bool respectTenantToggle = true)
    {
        try
        {
            if (!sender.IsConfigured) return false;
            if (respectTenantToggle && !await TenantEmailsEnabledAsync()) return false;
            return await sender.SendAsync(new EmailMessage(toEmail, toName, subject, body));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email send to {To} failed", toEmail);
            return false;
        }
    }

    /// <summary>Turn a relative in-app path ("/projects/3") into an absolute URL using
    /// the configured base. Returns the input unchanged if it is already absolute or no
    /// base is known.</summary>
    public string? AbsoluteLink(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        if (relative.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return relative;
        var baseUrl = BaseUrl();
        if (string.IsNullOrEmpty(baseUrl)) return relative;
        return $"{baseUrl.TrimEnd('/')}/{relative.TrimStart('/')}";
    }

    // ── internals ────────────────────────────────────────────────────────────────
    private async Task<bool> TenantEmailsEnabledAsync()
    {
        var s = await db.TenantSettings.FirstOrDefaultAsync();
        return s?.NotificationEmailsEnabled ?? true;   // default ON when no settings row
    }

    /// <summary>Configured <c>Email:AppBaseUrl</c>, else the first <c>AllowedOrigins</c>
    /// entry (the web app's own origin), else empty.</summary>
    private string BaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_o.AppBaseUrl)) return _o.AppBaseUrl.Trim();
        var origins = config.GetSection("AllowedOrigins").Get<string[]>();
        var first = origins?.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));
        return first?.Trim() ?? "";
    }

    private string ComposeBody(string? body, string? link)
    {
        var abs = AbsoluteLink(link);
        if (string.IsNullOrWhiteSpace(body)) return abs is null ? "" : abs;
        return abs is null ? body : $"{body}\n\n{abs}";
    }
}
