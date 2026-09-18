using Microsoft.EntityFrameworkCore;
using Remvora.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Remvora.Infrastructure;

/// <summary>Privileged maintenance bypasses tenant filters only for expired ephemeral credentials.
/// Audit events, devices, users and active sessions are never removed.</summary>
public sealed class RetentionService(Database db)
{
    public async Task<int> Sweep(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var count = await db.Challenges.IgnoreQueryFilters().Where(x => x.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.Enrollments.IgnoreQueryFilters().Where(x => x.ExpiresAt < cutoff && (x.ConsumedAt == null || db.Devices.IgnoreQueryFilters().Any(d => d.Id == x.DeviceId && d.EnrollmentStatus != EnrollmentStatus.PendingActivation))).ExecuteDeleteAsync(ct);
        count += await db.Sessions.IgnoreQueryFilters().Where(x => x.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.RemoteSessions.IgnoreQueryFilters().Where(x => x.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
        return count;
    }
}
public sealed class RetentionWorker(IServiceScopeFactory scopes, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        // First sweep is delayed so migrations/bootstrap and test-host creation never race maintenance.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<RetentionService>().Sweep(stoppingToken);
                logger.LogInformation("Expired credential maintenance removed {Count} records", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogError("Expired credential maintenance failed; retrying next interval"); }
        }
    }
}
