using System.Collections.Concurrent;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 21.1 — Test double for <see cref="IEmailSender"/>. Reports itself as configured
/// (so the email code paths run) and records every message instead of talking to a
/// real SMTP server. Tests filter by recipient address so each can isolate its own
/// captures from the shared host.
/// </summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public bool IsConfigured => true;

    public Task<bool> SendAsync(EmailMessage msg, CancellationToken ct = default)
    {
        _sent.Enqueue(msg);
        return Task.FromResult(true);
    }

    public IReadOnlyList<EmailMessage> To(string email) =>
        _sent.Where(m => string.Equals(m.ToEmail, email, StringComparison.OrdinalIgnoreCase)).ToList();

    public IReadOnlyList<EmailMessage> All() => _sent.ToList();
}
