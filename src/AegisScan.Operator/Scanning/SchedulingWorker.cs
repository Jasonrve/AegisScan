using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Resources;
using Cronos;
using KubeOps.KubernetesClient;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Drives the `schedule`/`fullScanSchedule` cron fields on RepositorySource/ScanPolicy CRDs that
/// reconciliation alone doesn't act on. RepositorySource re-discovery is triggered by bumping an
/// annotation (which fires a real reconcile through RepositorySourceController — no duplicated
/// discovery logic); ScanPolicy schedules directly queue executions for repositories referencing
/// that policy via `policyRef`. Runs under a Postgres advisory lock (<see cref="DistributedLock"/>)
/// so multiple replicas never double-queue the same scheduled scan.
/// </summary>
public sealed class SchedulingWorker(
    IServiceScopeFactory scopeFactory,
    IKubernetesClient client,
    ILogger<SchedulingWorker> logger) : BackgroundService
{
    private const long LockId = 7002;
    private const string LastScheduledAnnotation = "aegisscan.io/last-scheduled-discovery";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastCheck = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
                var acquired = await DistributedLock.TryRunExclusiveAsync(db, LockId, async () =>
                {
                    await ProcessRepositorySourcesAsync(lastCheck, now, stoppingToken);
                    await ProcessScanPoliciesAsync(db, lastCheck, now, stoppingToken);
                    await EnforceRetentionAsync(db, stoppingToken);
                }, stoppingToken);
                // Only advance the checkpoint when we actually held the lock and ran the check —
                // otherwise a replica that never wins would silently drift its own notion of "last checked".
                if (acquired) lastCheck = now;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduling worker iteration failed.");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProcessRepositorySourcesAsync(DateTime lastCheck, DateTime now, CancellationToken ct)
    {
        var sources = await client.ListAsync<V1RepositorySource>(cancellationToken: ct);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Spec.Schedule) || !IsDue(source.Spec.Schedule, lastCheck, now)) continue;
            source.Metadata.Annotations ??= new Dictionary<string, string>();
            source.Metadata.Annotations[LastScheduledAnnotation] = now.ToString("O");
            await client.UpdateAsync(source, ct);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Triggered scheduled re-discovery for RepositorySource {Name}.", source.Metadata.Name);
        }
    }

    private async Task ProcessScanPoliciesAsync(AegisDbContext db, DateTime lastCheck, DateTime now, CancellationToken ct)
    {
        var policies = await client.ListAsync<V1ScanPolicy>(cancellationToken: ct);
        foreach (var policy in policies)
        {
            var incrementalDue = !string.IsNullOrWhiteSpace(policy.Spec.Schedule) && IsDue(policy.Spec.Schedule, lastCheck, now);
            var fullDue = !string.IsNullOrWhiteSpace(policy.Spec.FullScanSchedule) && IsDue(policy.Spec.FullScanSchedule, lastCheck, now);
            if (!incrementalDue && !fullDue) continue;

            var repositories = await db.Repositories
                .Where(r => r.PolicyRef == policy.Metadata.Name && r.Status != "Disabled" && r.Status != "Error" && r.Status != "Blocked")
                .ToListAsync(ct);
            var scanner = policy.Spec.Scanners.FirstOrDefault() ?? "trufflehog";

            foreach (var repository in repositories)
            {
                if (fullDue) await QueueScanAsync(db, repository.Id, scanner, "full", ct);
                else if (incrementalDue) await QueueScanAsync(db, repository.Id, scanner, "incremental", ct);
            }
            if (repositories.Count > 0 && logger.IsEnabled(LogLevel.Information))
            {
                var mode = fullDue ? "full" : "incremental";
                logger.LogInformation("Scheduled {Mode} scan for {Count} repositor(y/ies) under ScanPolicy {Policy}.",
                    mode, repositories.Count, policy.Metadata.Name);
            }
        }
    }

    /// <summary>
    /// Deletes execution history older than the "retentionDays" setting (default 90). Scoped to
    /// execution rows only — findings and repositories are current-state data, not history, and
    /// aren't touched here.
    /// </summary>
    private async Task EnforceRetentionAsync(AegisDbContext db, CancellationToken ct)
    {
        var setting = await db.Settings.FindAsync(["retentionDays"], ct);
        var retentionDays = 90;
        if (setting is not null && int.TryParse(setting.Value, out var configured) && configured > 0) retentionDays = configured;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var deleted = await db.Executions
            .Where(e => (e.Status == "succeeded" || e.Status == "failed" || e.Status == "canceled") && e.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0 && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Retention sweep removed {Count} execution(s) older than {Days} day(s).", deleted, retentionDays);
    }

    private static Task QueueScanAsync(AegisDbContext db, string repositoryId, string scanner, string mode, CancellationToken ct)
    {
        db.Executions.Add(new ExecutionEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RepositoryId = repositoryId,
            Scanner = scanner,
            Mode = mode,
            Status = "queued",
            Attempt = 1,
            SanitizedLog = $"Scan queued by scheduled {mode} policy."
        });
        return Task.CompletedTask;
    }

    private static bool IsDue(string cronExpression, DateTime lastCheck, DateTime now)
    {
        try
        {
            var cron = CronExpression.Parse(cronExpression);
            var next = cron.GetNextOccurrence(lastCheck, TimeZoneInfo.Utc);
            return next.HasValue && next.Value <= now;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }
}
