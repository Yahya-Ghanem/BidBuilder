using System.Collections.Concurrent;

namespace BidBuilder.Api.Auth;

/// <summary>The outcome of a rate-limit check for one request.</summary>
/// <param name="Allowed">False → the caller has exceeded its per-minute budget (429).</param>
/// <param name="Limit">The configured per-minute limit, or null when unlimited.</param>
/// <param name="Remaining">Requests left in the current window (0 when blocked/limited; null when unlimited).</param>
/// <param name="RetryAfterSeconds">Seconds until the window resets (only meaningful when blocked).</param>
/// <param name="Count">Requests counted in the current window so far (for the usage view).</param>
public readonly record struct RateResult(bool Allowed, int? Limit, int? Remaining, int RetryAfterSeconds, int Count);

/// <summary>
/// 22.2 — In-process per-key request limiter (fixed one-minute window), keyed on the API
/// key's id. Mirrors the existing login fixed-window limiter's simplicity. A singleton, so
/// the window survives across requests but NOT across a process restart — limits reset on
/// deploy, which is acceptable for a throughput guard (a hard quota would need shared state).
/// Thread-safe via a per-key lock.
/// </summary>
public sealed class ApiKeyRateLimiter
{
    private sealed class Window { public long StartTicks; public int Count; }

    private static readonly long WindowTicks = TimeSpan.FromMinutes(1).Ticks;
    private readonly ConcurrentDictionary<int, Window> _windows = new();

    /// <summary>Account for one request and decide whether it's allowed. Unlimited keys
    /// (<paramref name="limitPerMinute"/> null or ≤0) are always allowed and not counted.</summary>
    public RateResult Check(int keyId, int? limitPerMinute, DateTime nowUtc)
    {
        if (limitPerMinute is not { } limit || limit <= 0)
            return new RateResult(true, null, null, 0, 0);

        var w = _windows.GetOrAdd(keyId, _ => new Window { StartTicks = nowUtc.Ticks, Count = 0 });
        lock (w)
        {
            if (nowUtc.Ticks - w.StartTicks >= WindowTicks)
            {
                w.StartTicks = nowUtc.Ticks;
                w.Count = 0;
            }
            if (w.Count >= limit)
            {
                var elapsed = nowUtc.Ticks - w.StartTicks;
                var retry = (int)Math.Ceiling((WindowTicks - elapsed) / (double)TimeSpan.TicksPerSecond);
                return new RateResult(false, limit, 0, Math.Max(1, retry), w.Count);
            }
            w.Count++;
            return new RateResult(true, limit, limit - w.Count, 0, w.Count);
        }
    }

    /// <summary>Requests counted for a key in the CURRENT window (0 if the window has
    /// lapsed). Read-only — used by the management list's usage column; never mutates state.</summary>
    public int CurrentCount(int keyId, DateTime nowUtc)
    {
        if (!_windows.TryGetValue(keyId, out var w)) return 0;
        lock (w)
        {
            return nowUtc.Ticks - w.StartTicks >= WindowTicks ? 0 : w.Count;
        }
    }
}
