using Microsoft.Extensions.DependencyInjection;

namespace BidBuilder.Api.Services;

/// <summary>
/// 22.1 — Timer-driven host that periodically sends due notification digests. It owns no
/// request scope, so each tick opens a fresh DI scope and delegates to <see cref="DigestService"/>
/// (whose reads bypass the tenant query filter and scope by explicit TenantId). The whole tick
/// is wrapped so a transient failure (e.g. a brief DB blip) is logged and retried on the next
/// interval rather than tearing down the host.
///
/// Registered only when <c>Digests:Enabled</c> (default true). Tests set it false and invoke
/// <see cref="DigestService"/> directly with a controlled "now", so digest assertions are
/// deterministic and no background thread outlives the test host (mirrors the Hangfire pattern).
/// </summary>
public sealed class DigestSchedulerService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<DigestSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Max(60, config.GetValue("Digests:IntervalSeconds", 900));
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        logger.LogInformation("Digest scheduler started (interval {Interval}s).", intervalSeconds);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var digest = scope.ServiceProvider.GetRequiredService<DigestService>();
                var sent = await digest.RunAllDueDigestsAsync(DateTime.UtcNow, stoppingToken);
                if (sent > 0) logger.LogInformation("Digest scheduler sent {Count} digest(s).", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // graceful shutdown
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Digest scheduler tick failed; will retry next interval.");
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
