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
    /// <summary>Name of the PVC holding the shared git mirrors (ADR 0001); unset = every Job clones directly.</summary>
    private readonly string? _gitCachePvc = Environment.GetEnvironmentVariable("AEGIS_GIT_CACHE_PVC");
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
        var capacity = await GetMaxConcurrencyAsync(db, ct) - await db.Executions.CountAsync(e => e.Status == "running", ct);
        if (capacity <= 0) return;
        var queued = await db.Executions.Where(e => e.Status == "queued" && (e.NotBefore == null || e.NotBefore <= now))
            .OrderBy(e => e.CreatedAt).Take(capacity).ToListAsync(ct);
        foreach (var execution in queued)
        {
            var repository = await db.Repositories.FindAsync([execution.RepositoryId], ct);
            if (repository is null)
            {
                execution.Status = "failed"; execution.FinishedAt = DateTimeOffset.UtcNow;
                execution.SanitizedLog = $"Repository '{execution.RepositoryId}' no longer exists.";
                continue;
            }
            var scanner = await db.Scanners.FindAsync([execution.Scanner], ct);
            if (scanner is null || !scanner.Enabled)
            {
                execution.Status = "failed"; execution.FinishedAt = DateTimeOffset.UtcNow;
                execution.SanitizedLog = scanner is null ? $"Scanner '{execution.Scanner}' does not exist." : $"Scanner '{execution.Scanner}' is disabled.";
                continue;
            }

            try
            {
                var pullRequests = scanner.Id == "renovate" && ScanJobFactory.SupportsPullRequests(repository) && await IsSettingEnabledAsync(db, "renovateCreatePullRequests", ct);
                var forgeTokenKey = pullRequests ? await FindForgeTokenKeyAsync(repository.Provider.ToLowerInvariant(), ct) : null;
                if (pullRequests && forgeTokenKey is null)
                {
                    execution.Status = "failed"; execution.FinishedAt = DateTimeOffset.UtcNow;
                    execution.FinishedAt = DateTimeOffset.UtcNow;
                    execution.SanitizedLog = $"Pull request mode is enabled but Secret '{ScanJobFactory.CredentialsSecretName}' has neither 'renovate-token-{repository.Provider.ToLowerInvariant()}' nor 'renovate-token' (a forge token with repository and issue write access).";
                    continue;
                }
                var images = scanner.Id == SbomScan.ScannerId ? await db.RepositoryImages.Where(i => i.RepositoryId == repository.Id).Select(i => i.Ref).ToListAsync(ct) : null;
                var job = ScanJobFactory.Build(execution, repository, scanner, _namespace, pullRequests, forgeTokenKey, _gitCachePvc, images);
                await kubernetes.BatchV1.CreateNamespacedJobAsync(job, _namespace, cancellationToken: ct);
                execution.JobName = job.Metadata.Name;
                execution.Status = "running";
                execution.StartedAt = DateTimeOffset.UtcNow;
                execution.SanitizedLog = $"Scan Job '{job.Metadata.Name}' created{(pullRequests ? " (pull request mode)" : "")}.";
            }
            catch (HttpOperationException ex)
            {
                execution.Status = "failed"; execution.FinishedAt = DateTimeOffset.UtcNow;
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
            if (job is null)
            {
                // Not visible yet (just created) is fine; but a Job that stays missing was deleted or expired, and would hold a concurrency slot forever.
                if (execution.StartedAt is { } startedAt && DateTimeOffset.UtcNow - startedAt > TimeSpan.FromMinutes(2))
                {
                    execution.Status = "failed";
                    execution.FinishedAt = DateTimeOffset.UtcNow;
                    execution.SanitizedLog = "The scan Job disappeared before reporting a result (deleted or expired); re-queue the scan.";
                    ScanMetrics.ExecutionsTotal.WithLabels("failed").Inc();
                }
                continue;
            }

            var succeeded = job.Status?.Succeeded is > 0;
            var failed = job.Status?.Failed is > 0;
            if (!succeeded && !failed) continue; // still running

            var scannerDef = await db.Scanners.AsNoTracking().FirstOrDefaultAsync(s => s.Id == execution.Scanner, ct);
            var generic = scannerDef is not null && !scannerDef.BuiltIn && scannerDef.OutputFormat != "native";
            // Built-in parsers need the whole (debug-level) log; only custom scanner logs (and the SBOM dump, whose size follows the images) are capped.
            var capped = generic || execution.Scanner == SbomScan.ScannerId;
            var log = await ReadContainerLogAsync(execution.JobName!, ScanJobFactory.ScanContainerName, ct, capped ? GenericResultParser.MaxLogBytes : null);
            var logTruncated = capped && System.Text.Encoding.UTF8.GetByteCount(log) >= GenericResultParser.MaxLogBytes;
            // Many scanners (gitleaks, trivy --exit-code, semgrep --error) exit non-zero when they FIND something: the exit codes the
            // scanner declares as "the scan ran" count as success, anything else (crash, clone failure: no exit code) stays a failure.
            if (!succeeded && generic && await ReadScanExitCodeAsync(execution.JobName!, ct) is { } exitCode
                && (JsonSerializer.Deserialize<List<int>>(scannerDef!.SuccessExitCodesJson) ?? [0]).Contains(exitCode))
                succeeded = true;
            if (succeeded)
            {
                var commit = ExtractCommitSha(await ReadContainerLogAsync(execution.JobName!, ScanJobFactory.CloneContainerName, ct));
                var result = capped && logTruncated ? GenericResultParser.OutputTooLarge()
                    : generic
                    ? GenericResultParser.Parse(scannerDef!.OutputFormat, log, execution.RepositoryId, scannerDef)
                    : execution.Scanner switch
                    {
                        "trufflehog" => TruffleHogResultParser.Parse(log, execution.RepositoryId),
                        "renovate" => RenovateResultParser.Parse(log, execution.RepositoryId),
                        SbomScan.ScannerId => SbomResultParser.Parse(log, execution.RepositoryId, scannerDef!),
                        _ => new ScanParseResult([], false, "Scanner finished; its output is not interpreted.") { NotInterpreted = true }
                    };
                if (result.Error is not null)
                {
                    // Deterministic: the same output would fail to parse again, so no retries.
                    execution.Status = "failed";
                    execution.OutputLog = result.Error + "\n" + await BuildFailureOutputAsync(execution, log, ct);
                    execution.SanitizedLog = result.Error;
                    ScanMetrics.ExecutionsTotal.WithLabels("failed").Inc();
                    execution.FinishedAt = job.Status?.CompletionTime ?? DateTimeOffset.UtcNow;
                    await NotifyWebhookAsync(db, execution, ct);
                    continue;
                }
                foreach (var finding in result.Findings) finding.Category = scannerDef?.Category ?? finding.Category;
                execution.OutputInterpreted = !result.NotInterpreted;
                var hasOpenVerified = await UpsertFindingsAsync(db, execution, result, commit, ct);
                if (result.Sboms is not null) await SbomStore.UpsertAsync(db, execution, commit, result.Sboms, ct);
                string? enrichmentWarning = null;
                if (await db.Repositories.FindAsync([execution.RepositoryId], ct) is { } enrichRepository)
                    enrichmentWarning = await FindingEnricher.EnrichAsync(db, httpClientFactory, enrichRepository, commit, result.Dependencies, ct);
                execution.Status = "succeeded";
                execution.FindingCount = result.Findings.Count;
                execution.CommitSHA = commit ?? "";
                if (result.Links is { Count: > 0 }) execution.LinksJson = JsonSerializer.Serialize(result.Links);
                execution.OutputLog = enrichmentWarning is null ? result.Output : result.Output + enrichmentWarning + "\n";
                execution.SanitizedLog = result.NotInterpreted ? "Scan completed; this scanner's output is not interpreted, so no findings are reported." : $"Scan completed. {result.Findings.Count} finding(s) detected.";

                var repository = await db.Repositories.FindAsync([execution.RepositoryId], ct);
                if (repository is not null && commit is not null) repository.LatestCommitSHA = commit;
                if (repository is not null && _gitCachePvc is not null) repository.CacheState = "warm";
                // Block only for verified findings that are still open: an ignored / false-positive decision must be able to stick.
                if (hasOpenVerified && await IsBlockVerifiedFindingsEnabledAsync(db, ct))
                {
                    if (repository is not null && repository.Status != "Blocked")
                    {
                        repository.Status = "Blocked";
                        db.ClearAlertAck($"repo:{execution.RepositoryId}");
                        logger.LogWarning("Repository {RepositoryId} blocked: verified finding detected.", execution.RepositoryId);
                    }
                }
            }
            else if (execution.Attempt < MaxAttempts)
            {
                execution.OutputLog = await BuildFailureOutputAsync(execution, log, ct);
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
                execution.OutputLog = await BuildFailureOutputAsync(execution, log, ct);
                execution.SanitizedLog = $"Scan Job failed after {execution.Attempt} attempt(s). See run output for detail.";
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

    /// <summary>POSTs a scan-completion summary to the configured webhook (signed when a secret is set). Best-effort — failures are logged, never surfaced to the execution.</summary>
    private async Task NotifyWebhookAsync(AegisDbContext db, ExecutionEntity execution, CancellationToken ct)
    {
        var (ok, detail) = await WebhookNotifier.SendAsync(db, httpClientFactory, new
        {
            @event = "scan.completed",
            executionId = execution.Id,
            repositoryId = execution.RepositoryId,
            scanner = execution.Scanner,
            mode = execution.Mode,
            status = execution.Status,
            findingCount = execution.FindingCount,
            commit = execution.CommitSHA
        }, ct);
        if (!ok && !detail.StartsWith("No webhook URL", StringComparison.Ordinal))
            logger.LogWarning("Webhook notification for execution {ExecutionId} failed: {Detail}.", execution.Id, detail);
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

    /// <summary>Upserts the scan's findings and auto-resolves the ones it no longer reports; returns whether any verified finding is open afterwards.</summary>
    public static async Task<bool> UpsertFindingsAsync(AegisDbContext db, ExecutionEntity execution, ScanParseResult result, string? commit, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var hasOpenVerified = false;
        foreach (var finding in result.Findings)
        {
            var existing = await db.Findings.FindAsync([finding.Fingerprint], ct);
            if (existing is null)
            {
                finding.FirstSeenAt = finding.LastSeenAt = now;
                finding.ExecutionId = execution.Id;
                finding.CommitSHA = commit ?? "";
                db.Findings.Add(finding);
                ScanMetrics.FindingsTotal.WithLabels(finding.Scanner is "trufflehog" or "renovate" ? finding.Detector : finding.Scanner).Inc(); // custom detectors (CVE ids) would explode the label cardinality
                hasOpenVerified |= finding.Verified;
                continue;
            }
            existing.Verified = finding.Verified;
            existing.Scanner = finding.Scanner;
            existing.SafePath = finding.SafePath;
            existing.Detail = finding.Detail;
            existing.UpdateType = finding.UpdateType;
            existing.Preview = finding.Preview;
            existing.SourceUrl = finding.SourceUrl;
            existing.Category = finding.Category;
            existing.HelpUrl = finding.HelpUrl;
            if (finding.Severity.Length > 0) existing.Severity = finding.Severity;
            if (finding.SourceLine is not null) existing.SourceLine = finding.SourceLine;
            // A forge-connected run knows the current PRs (a closed PR must stop being linked); a read-only run knows nothing about them.
            if (result.PullRequestMode || finding.PullRequestNumber is not null) existing.PullRequestNumber = finding.PullRequestNumber;
            existing.LastSeenAt = now;
            existing.ExecutionId = execution.Id;
            // Still present: record the commit it was last seen at, so links keep pointing at a revision that has it.
            if (commit is not null) existing.CommitSHA = commit;
            // Only a fix reappearing re-opens a finding; ignored / false-positive triage decisions are kept.
            if (existing.State == "resolved") { existing.State = "open"; existing.ResolvedAt = null; db.ClearAlertAck($"finding:{existing.Fingerprint}"); }
            if (existing.IntroducedCommit.Length == 0) existing.IntroducedCommit = finding.IntroducedCommit;
            hasOpenVerified |= existing.Verified && existing.State == "open";
        }

        if (!result.Authoritative) return hasOpenVerified;
        // The scanner saw the whole repository and no longer reports these: they are fixed.
        var seen = result.Findings.Select(f => f.Fingerprint).ToHashSet();
        if (result.KeepOpen is not null) seen.UnionWith(result.KeepOpen);
        var open = await db.Findings
            .Where(f => f.RepositoryId == execution.RepositoryId && f.Scanner == execution.Scanner && f.State == "open")
            .ToListAsync(ct);
        // A secret deleted from the latest files is NOT fixed — it still lives in git history and must be rotated — so only a full
        // (history) scan may resolve secret findings. Incremental runs resolve dependency findings, which are about the current files.
        foreach (var finding in open.Where(f => !seen.Contains(f.Fingerprint) && (execution.Mode == "full" || !(f.Category == "secrets" || f.Scanner == "trufflehog"))))
        { finding.State = "resolved"; finding.ResolvedAt = now; }
        return hasOpenVerified;
    }

    /// <summary>The clone container prints the checked-out commit as its last line (see ScanJobFactory).</summary>
    private static string? ExtractCommitSha(string cloneLog) =>
        cloneLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
            .LastOrDefault(l => l.Length == 40 && l.All(Uri.IsHexDigit));

    private async Task<string> BuildFailureOutputAsync(ExecutionEntity execution, string scanLog, CancellationToken ct)
    {
        var cloneLog = await ReadContainerLogAsync(execution.JobName!, ScanJobFactory.CloneContainerName, ct);
        // Scan output of a failed run may contain raw scanner JSON, so only its non-JSON (error/stderr) lines are kept.
        // Custom scanners get the same treatment: their stdout JSON/SARIF may hold secrets, their stderr diagnostics are what explains a failure.
        var errorLines = string.Join('\n', scanLog.Split('\n').Where(l => l.Length > 0 && !l.TrimStart().StartsWith('{') && !l.TrimStart().StartsWith('"')).Select(l => l.Length > 300 ? l[..300] + "…" : l).TakeLast(40));
        return ScanOutputSanitizer.Sanitize($"[clone]\n{cloneLog.Trim()}\n\n[scan]\n{errorLines}".Trim());
    }

    private static async Task<bool> IsSettingEnabledAsync(AegisDbContext db, string key, CancellationToken ct)
    {
        var setting = await db.Settings.FindAsync([key], ct);
        return setting is not null && setting.Value == "true";
    }

    /// <summary>Picks the forge token key for a provider: `renovate-token-&lt;provider&gt;` wins over the generic `renovate-token`, so a mixed GitHub/GitLab/Gitea estate can hold one token each.</summary>
    private async Task<string?> FindForgeTokenKeyAsync(string provider, CancellationToken ct)
    {
        try
        {
            var secret = await kubernetes.CoreV1.ReadNamespacedSecretAsync(ScanJobFactory.CredentialsSecretName, _namespace, cancellationToken: ct);
            return new[] { $"renovate-token-{provider}", ScanJobFactory.ForgeTokenKey }.FirstOrDefault(key => secret.Data?.ContainsKey(key) == true);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Reads the "maxConcurrency" setting (default 4) — the cap on simultaneously running scan Jobs.</summary>
    private static async Task<int> GetMaxConcurrencyAsync(AegisDbContext db, CancellationToken ct)
    {
        var setting = await db.Settings.FindAsync(["maxConcurrency"], ct);
        return setting is not null && int.TryParse(setting.Value, out var n) && n > 0 ? n : 4;
    }

    /// <summary>Exit code of the finished scan container, or null when it never ran (e.g. the clone init container failed).</summary>
    private async Task<int?> ReadScanExitCodeAsync(string jobName, CancellationToken ct)
    {
        var pods = await kubernetes.CoreV1.ListNamespacedPodAsync(_namespace, labelSelector: $"job-name={jobName}", cancellationToken: ct);
        return pods.Items.FirstOrDefault()?.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == ScanJobFactory.ScanContainerName)?.State?.Terminated?.ExitCode;
    }

    private async Task<string> ReadContainerLogAsync(string jobName, string container, CancellationToken ct, int? limitBytes = null)
    {
        var pods = await kubernetes.CoreV1.ListNamespacedPodAsync(_namespace, labelSelector: $"job-name={jobName}", cancellationToken: ct);
        var pod = pods.Items.FirstOrDefault();
        if (pod is null) return "";
        try
        {
            await using var stream = await kubernetes.CoreV1.ReadNamespacedPodLogAsync(pod.Metadata.Name, _namespace, container: container, limitBytes: limitBytes, cancellationToken: ct);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (HttpOperationException ex)
        {
            // e.g. the scan container never started because the clone init container failed.
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug(ex, "Could not read {Container} logs for job {Job}.", container, jobName);
            return "";
        }
    }
}
