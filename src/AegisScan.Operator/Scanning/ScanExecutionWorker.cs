using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Drives queued executions to completion: creates a Kubernetes Job per execution (clone + scan),
/// polls for Job completion, parses results into Findings, and updates execution status.
/// Also reconciles executions left "running" by a previous process instance on startup.
/// Every tick runs under a Postgres advisory lock (<see cref="DistributedLock"/>) so multiple
/// replicas never create duplicate Jobs for the same execution.
/// </summary>
public sealed class ScanExecutionWorker(
    IServiceScopeFactory scopeFactory,
    IKubernetes kubernetes,
    IHttpClientFactory httpClientFactory,
    ILogger<ScanExecutionWorker> logger) : BackgroundService
{
    private const long LockId = 7001;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private readonly string _namespace = Environment.GetEnvironmentVariable("AEGIS_KUBE_NAMESPACE") ?? "default";
    private readonly Random _random = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunLockedAsync(db => ReconcileOrphanedExecutionsAsync(db, stoppingToken), stoppingToken);
        }
        catch (Exception ex)
        {
            // Never let a startup-only failure take down the whole host (BackgroundServiceExceptionBehavior
            // defaults to StopHost) — worst case we just skip one-time orphan cleanup this process lifetime.
            logger.LogError(ex, "Startup orphaned-execution reconciliation failed.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunLockedAsync(async db =>
                {
                    await ProcessQueuedAsync(db, stoppingToken);
                    await ProcessRunningAsync(db, stoppingToken);
                }, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan execution worker iteration failed.");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task RunLockedAsync(Func<AegisDbContext, Task> action, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
        await DistributedLock.TryRunExclusiveAsync(db, LockId, () => action(db), ct);
    }

    private async Task ReconcileOrphanedExecutionsAsync(AegisDbContext db, CancellationToken ct)
    {
        var running = await db.Executions.Where(e => e.Status == "running" || e.Status == "preparing").ToListAsync(ct);
        foreach (var execution in running)
        {
            var jobStillExists = execution.JobName is not null && await TryGetJobAsync(execution.JobName, ct) is not null;
            if (!jobStillExists)
            {
                execution.Status = "failed";
                execution.FinishedAt = DateTimeOffset.UtcNow;
                execution.SanitizedLog = "Execution abandoned: no active Job found after operator restart.";
                logger.LogWarning("Marking orphaned execution {ExecutionId} as failed on startup.", execution.Id);
            }
        }
    }

    private async Task ProcessQueuedAsync(AegisDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var queued = await db.Executions.Where(e => e.Status == "queued" && (e.NotBefore == null || e.NotBefore <= now)).ToListAsync(ct);
        foreach (var execution in queued)
        {
            var repository = await db.Repositories.FindAsync([execution.RepositoryId], ct);
            if (repository is null)
            {
                execution.Status = "failed";
                execution.SanitizedLog = $"Repository '{execution.RepositoryId}' no longer exists.";
                continue;
            }
            var scanner = await db.Scanners.FindAsync([execution.Scanner], ct);
            if (scanner is null || !scanner.Enabled)
            {
                execution.Status = "failed";
                execution.SanitizedLog = scanner is null ? $"Scanner '{execution.Scanner}' does not exist." : $"Scanner '{execution.Scanner}' is disabled.";
                continue;
            }

            try
            {
                var job = ScanJobFactory.Build(execution, repository, scanner, _namespace);
                await kubernetes.BatchV1.CreateNamespacedJobAsync(job, _namespace, cancellationToken: ct);
                execution.JobName = job.Metadata.Name;
                execution.Status = "running";
                execution.StartedAt = DateTimeOffset.UtcNow;
                execution.SanitizedLog = $"Scan Job '{job.Metadata.Name}' created.";
            }
            catch (HttpOperationException ex)
            {
                execution.Status = "failed";
                execution.SanitizedLog = $"Failed to create scan Job: {ex.Response.StatusCode}.";
                logger.LogError(ex, "Failed to create Job for execution {ExecutionId}.", execution.Id);
            }
        }
    }

    private async Task ProcessRunningAsync(AegisDbContext db, CancellationToken ct)
    {
        var running = await db.Executions.Where(e => e.Status == "running" && e.JobName != null).ToListAsync(ct);
        foreach (var execution in running)
        {
            var job = await TryGetJobAsync(execution.JobName!, ct);
            if (job is null) continue; // TTL already reaped it, or not visible yet; leave running

            var succeeded = job.Status?.Succeeded is > 0;
            var failed = job.Status?.Failed is > 0;
            if (!succeeded && !failed) continue; // still running

            var log = await ReadScanContainerLogAsync(execution.JobName!, ct);
            if (succeeded)
            {
                var findings = execution.Scanner switch
                {
                    "trufflehog" => TruffleHogResultParser.Parse(log, execution.RepositoryId),
                    "renovate" => RenovateResultParser.Parse(log, execution.RepositoryId),
                    _ => []
                };
                foreach (var finding in findings)
                {
                    var existing = await db.Findings.FindAsync([finding.Fingerprint], ct);
                    if (existing is null) db.Findings.Add(finding);
                    else { existing.Verified = finding.Verified; existing.State = "open"; }
                    ScanMetrics.FindingsTotal.WithLabels(finding.Detector).Inc();
                }
                execution.Status = "succeeded";
                execution.SanitizedLog = $"Scan completed. {findings.Count} finding(s) detected.";

                if (findings.Any(f => f.Verified) && await IsBlockVerifiedFindingsEnabledAsync(db, ct))
                {
                    var repository = await db.Repositories.FindAsync([execution.RepositoryId], ct);
                    if (repository is not null && repository.Status != "Blocked")
                    {
                        repository.Status = "Blocked";
                        logger.LogWarning("Repository {RepositoryId} blocked: verified finding detected.", execution.RepositoryId);
                    }
                }
            }
            else if (execution.Attempt < MaxAttempts)
            {
                // Retry with jittered exponential backoff instead of dead-lettering immediately.
                var backoffSeconds = Math.Pow(2, execution.Attempt) * 10 * (0.8 + _random.NextDouble() * 0.4);
                execution.Attempt++;
                execution.Status = "queued";
                execution.JobName = null;
                execution.NotBefore = DateTimeOffset.UtcNow.AddSeconds(backoffSeconds);
                execution.SanitizedLog = $"Scan Job failed; retrying (attempt {execution.Attempt}/{MaxAttempts}) in ~{backoffSeconds:F0}s.";
            }
            else
            {
                // Exhausted retries: this is the dead-letter outcome. It stays "failed" until someone
                // re-queues it manually (POST /runs) — no further automatic attempts.
                execution.Status = "failed";
                execution.SanitizedLog = $"Scan Job failed after {execution.Attempt} attempt(s). See Job logs for detail.";
            }
            ScanMetrics.ExecutionsTotal.WithLabels(execution.Status).Inc();
            if (job.Status?.StartTime is { } started) execution.StartedAt = started;
            if (execution.Status is "succeeded" or "failed") execution.FinishedAt = job.Status?.CompletionTime ?? DateTimeOffset.UtcNow;
            if (job.Status?.StartTime is { } startedForMetrics && job.Status?.CompletionTime is { } completed)
                ScanMetrics.JobDurationSeconds.Observe((completed - startedForMetrics).TotalSeconds);

            if (execution.Status is "succeeded" or "failed") await NotifyWebhookAsync(db, execution, ct);
        }

        var queuedCount = await db.Executions.CountAsync(e => e.Status == "queued", ct);
        var runningCount = await db.Executions.CountAsync(e => e.Status == "running", ct);
        ScanMetrics.QueueDepth.WithLabels("queued").Set(queuedCount);
        ScanMetrics.QueueDepth.WithLabels("running").Set(runningCount);
    }

    /// <summary>Reads the "blockVerifiedFindings" policy setting; defaults to true (matching PolicyView's default) when unset.</summary>
    private static async Task<bool> IsBlockVerifiedFindingsEnabledAsync(AegisDbContext db, CancellationToken ct)
    {
        var setting = await db.Settings.FindAsync(["blockVerifiedFindings"], ct);
        return setting is null || JsonSerializer.Deserialize<bool>(setting.Value);
    }

    /// <summary>POSTs a scan-completion summary to the configured webhook URL (Settings key "webhookUrl"), if any. Best-effort — failures are logged, never surfaced to the execution.</summary>
    private async Task NotifyWebhookAsync(AegisDbContext db, ExecutionEntity execution, CancellationToken ct)
    {
        var setting = await db.Settings.FindAsync(["webhookUrl"], ct);
        if (setting is null) return;
        var url = JsonSerializer.Deserialize<string>(setting.Value);
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                @event = "scan.completed",
                executionId = execution.Id,
                repositoryId = execution.RepositoryId,
                scanner = execution.Scanner,
                mode = execution.Mode,
                status = execution.Status
            });
            using var client = httpClientFactory.CreateClient("resilient");
            using var response = await client.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Webhook notification for execution {ExecutionId} returned {StatusCode}.", execution.Id, response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Webhook notification for execution {ExecutionId} failed.", execution.Id);
        }
    }

    private async Task<V1Job?> TryGetJobAsync(string jobName, CancellationToken ct)
    {
        try
        {
            return await kubernetes.BatchV1.ReadNamespacedJobAsync(jobName, _namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<string> ReadScanContainerLogAsync(string jobName, CancellationToken ct)
    {
        var pods = await kubernetes.CoreV1.ListNamespacedPodAsync(_namespace, labelSelector: $"job-name={jobName}", cancellationToken: ct);
        var pod = pods.Items.FirstOrDefault();
        if (pod is null) return "";
        try
        {
            await using var stream = await kubernetes.CoreV1.ReadNamespacedPodLogAsync(pod.Metadata.Name, _namespace, container: ScanJobFactory.ScanContainerName, cancellationToken: ct);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (HttpOperationException ex)
        {
            logger.LogWarning(ex, "Could not read logs for pod {Pod}.", pod.Metadata.Name);
            return "";
        }
    }
}
