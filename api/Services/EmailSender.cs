using System.Net;
using System.Net.Mail;

namespace BidBuilder.Api.Services;

/// <summary>
/// 21.1 — Outbound email transport configuration, bound from the <c>Email</c>
/// config section (env vars use the <c>Email__*</c> form). All values are blank /
/// disabled by default, so a deployment that doesn't set them simply never sends
/// email (the senders below no-op). The SMTP password is a secret and must come
/// from the environment / a secret store — never appsettings in source control.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>Master switch. Even with a host set, email is only sent when true.</summary>
    public bool   Enabled     { get; set; }
    public string Host        { get; set; } = "";
    public int    Port        { get; set; } = 587;
    public string Username    { get; set; } = "";
    public string Password    { get; set; } = "";
    public bool   UseSsl      { get; set; } = true;
    /// <summary>The envelope From address, e.g. <c>noreply@bidbuilder.app</c>.</summary>
    public string FromAddress { get; set; } = "";
    public string FromName    { get; set; } = "BidBuilder";
    /// <summary>Absolute base URL used to turn the relative in-app links carried by
    /// notifications (e.g. <c>/projects/3</c>) into clickable URLs in emails. When
    /// blank, <see cref="EmailService"/> falls back to the first configured CORS
    /// origin (<c>AllowedOrigins</c>).</summary>
    public string AppBaseUrl  { get; set; } = "";
}

/// <summary>A single message to deliver. Body is plain text (kept deliberately
/// simple — no HTML templating engine), which every mail client renders safely.</summary>
public readonly record struct EmailMessage(string ToEmail, string? ToName, string Subject, string Body);

/// <summary>
/// 21.1 — Sends one email. Abstracted behind an interface (exactly like
/// <see cref="IWebhookSender"/>) so tests can capture deliveries without a real
/// SMTP server. <see cref="IsConfigured"/> lets callers cheaply skip all the
/// recipient-resolution work when email isn't set up.
/// </summary>
public interface IEmailSender
{
    /// <summary>True when a transport is configured and enabled; false → every
    /// <see cref="SendAsync"/> is a no-op that returns false.</summary>
    bool IsConfigured { get; }
    Task<bool> SendAsync(EmailMessage msg, CancellationToken ct = default);
}

/// <summary>
/// Production SMTP transport over <see cref="System.Net.Mail"/> (BCL — no
/// third-party mail dependency, mirroring the first-party SignedXml choice for
/// SAML). Failures are logged and swallowed (return false): an email must never
/// break the action that triggered it.
/// </summary>
public sealed class SmtpEmailSender(Microsoft.Extensions.Options.IOptions<EmailOptions> opt, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _o = opt.Value;

    public bool IsConfigured =>
        _o.Enabled
        && !string.IsNullOrWhiteSpace(_o.Host)
        && !string.IsNullOrWhiteSpace(_o.FromAddress);

    public async Task<bool> SendAsync(EmailMessage msg, CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        if (string.IsNullOrWhiteSpace(msg.ToEmail)) return false;

        try
        {
            using var client = new SmtpClient(_o.Host, _o.Port) { EnableSsl = _o.UseSsl };
            if (!string.IsNullOrWhiteSpace(_o.Username))
                client.Credentials = new NetworkCredential(_o.Username, _o.Password);

            using var mail = new MailMessage
            {
                From = new MailAddress(_o.FromAddress, _o.FromName),
                Subject = msg.Subject,
                Body = msg.Body,
                IsBodyHtml = false,
            };
            mail.To.Add(string.IsNullOrWhiteSpace(msg.ToName)
                ? new MailAddress(msg.ToEmail)
                : new MailAddress(msg.ToEmail, msg.ToName));

            await client.SendMailAsync(mail, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP send to {To} failed", msg.ToEmail);
            return false;
        }
    }
}
