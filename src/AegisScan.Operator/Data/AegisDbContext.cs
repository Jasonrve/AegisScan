using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Data;

public sealed class AegisDbContext(DbContextOptions<AegisDbContext> options) : DbContext(options)
{
    public DbSet<RepositoryEntity> Repositories => Set<RepositoryEntity>();
    public DbSet<ExecutionEntity> Executions => Set<ExecutionEntity>();
    public DbSet<FindingEntity> Findings => Set<FindingEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<AuditEntity> AuditEvents => Set<AuditEntity>();
    public DbSet<ScannerEntity> Scanners => Set<ScannerEntity>();
    public DbSet<RoleMappingEntity> RoleMappings => Set<RoleMappingEntity>();
    public DbSet<AlertAckEntity> AlertAcks => Set<AlertAckEntity>();
    public DbSet<SbomEntity> Sboms => Set<SbomEntity>();
    public DbSet<RepositoryImageEntity> RepositoryImages => Set<RepositoryImageEntity>();
    public DbSet<WebhookEntity> Webhooks => Set<WebhookEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FindingEntity>().HasKey(f => f.Fingerprint);
        modelBuilder.Entity<SettingEntity>().HasKey(s => s.Key);
        modelBuilder.Entity<AuditEntity>().HasIndex(a => a.Timestamp);
        modelBuilder.Entity<RoleMappingEntity>().HasKey(m => m.IdpGroup);
        modelBuilder.Entity<AlertAckEntity>().HasKey(a => a.Key);
        modelBuilder.Entity<SbomEntity>().HasKey(s => s.Id);
        modelBuilder.Entity<SbomEntity>().HasIndex(s => s.RepositoryId);
        modelBuilder.Entity<RepositoryImageEntity>().HasKey(i => new { i.RepositoryId, i.Ref });
        modelBuilder.Entity<WebhookEntity>().HasKey(w => w.Id);

        modelBuilder.Entity<SettingEntity>().HasData(
            new SettingEntity { Key = "retentionDays", Value = "90" });

        modelBuilder.Entity<ScannerEntity>().HasData(
            new ScannerEntity { Id = "trufflehog", Name = "TruffleHog", Image = "trufflesecurity/trufflehog:3.90.8", CommandJson = "[\"trufflehog\",\"filesystem\",\"{{workspace}}\",\"--json\",\"--no-update\"]", ArgsJson = "[]", BuiltIn = true, Enabled = true, OutputFormat = "native", Category = "secrets", SuccessExitCodesJson = "[0]" },
            new ScannerEntity { Id = "renovate", Name = "Renovate", Image = "renovate/renovate:44.155.7", CommandJson = "[]", ArgsJson = "[\"--platform=local\",\"--report-type=logging\",\"--dry-run=lookup\"]", BuiltIn = true, Enabled = true, OutputFormat = "native", Category = "dependencies", SuccessExitCodesJson = "[0]" },
            // The command is built in code (SbomScan.Script) so it ships with the operator; the image is the part an admin may repin.
            new ScannerEntity { Id = "sbom", Name = "SBOM (Trivy)", Image = "aquasec/trivy:0.58.1", CommandJson = "[]", ArgsJson = "[]", BuiltIn = true, Enabled = true, OutputFormat = "native", Category = "vulnerabilities", SuccessExitCodesJson = "[0]" });
    }
}

public static class AegisDbContextExtensions
{
    /// <summary>Removes a repository together with its findings and execution history (they reference it by id, with no FK). Caller saves.</summary>
    /// <summary>Forgets an alert acknowledgement so the alert shows again when its underlying signal changes (re-blocked repository, reopened finding). Caller saves.</summary>
    public static void ClearAlertAck(this AegisDbContext db, string key)
    {
        var ack = db.AlertAcks.Find(key);
        if (ack is not null) db.AlertAcks.Remove(ack);
    }

    public static void RemoveRepositoryWithData(this AegisDbContext db, RepositoryEntity repository)
    {
        db.Findings.RemoveRange(db.Findings.Where(f => f.RepositoryId == repository.Id));
        db.Executions.RemoveRange(db.Executions.Where(e => e.RepositoryId == repository.Id));
        db.Sboms.RemoveRange(db.Sboms.Where(s => s.RepositoryId == repository.Id));
        db.RepositoryImages.RemoveRange(db.RepositoryImages.Where(i => i.RepositoryId == repository.Id));
        db.Repositories.Remove(repository);
    }
}

public sealed class RepositoryEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string CloneUrl { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
    public string Status { get; set; } = "Ready";
    public string CacheState { get; set; } = "Unknown";
    public int ActiveFindingCount { get; set; }
    public string LatestCommitSHA { get; set; } = "";
    /// <summary>Name of the owning ScanPolicy CRD, if any (drives scheduled scans — see SchedulingWorker).</summary>
    public string? PolicyRef { get; set; }
    /// <summary>Cached JSON object of bytes-per-language (GitHub's /repos/{owner}/{repo}/languages shape) — see ApiEndpoints' languages endpoint.</summary>
    public string? LanguagesJson { get; set; }
}

public sealed class ExecutionEntity
{
    public string Id { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string Scanner { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "";
    public int Attempt { get; set; } = 1;
    public string SanitizedLog { get; set; } = "";
    public string? JobName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When set and in the future, this execution is a queued retry not yet due — see ScanExecutionWorker's backoff.</summary>
    public DateTimeOffset? NotBefore { get; set; }
    /// <summary>Set when the scan Job is created (queued -> running) — see ScanExecutionWorker.ProcessQueuedAsync.</summary>
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>Set when the execution reaches a terminal status (succeeded/failed/canceled) — see ScanExecutionWorker.ProcessRunningAsync.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Sanitized scanner/clone output (secrets stripped, size-capped) — see ScanOutputSanitizer.</summary>
    public string? OutputLog { get; set; }
    /// <summary>Number of findings the scan reported (open or not); set when the execution succeeds.</summary>
    public int FindingCount { get; set; }
    /// <summary>Commit the scan ran against (printed by the clone init container).</summary>
    public string CommitSHA { get; set; } = "";
    /// <summary>False when the scanner's output format is "none": the run succeeded but its findings are unknown (the UI shows a warning, not "0").</summary>
    public bool OutputInterpreted { get; set; } = true;
    /// <summary>JSON list of <see cref="AegisScan.KubeOps.Scanning.RunLink"/> (pull requests / issues a scan opened); resolved to URLs when served.</summary>
    public string? LinksJson { get; set; }
}

public sealed class FindingEntity
{
    public string Fingerprint { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string Detector { get; set; } = "";
    public bool Verified { get; set; }
    public string SafePath { get; set; } = "";
    public string CommitSHA { get; set; } = "";
    public string State { get; set; } = "open";
    /// <summary>"trufflehog" or "renovate" — scopes auto-resolve of findings a rescan no longer reports.</summary>
    public string Scanner { get; set; } = "";
    /// <summary>Human-readable specifics, e.g. "eslint 8.47.0 -> 10.12.0" for a Renovate update.</summary>
    public string Detail { get; set; } = "";
    /// <summary>Renovate update type (major/minor/patch/...); empty for other findings. Feeds <see cref="FindingSeverity"/>.</summary>
    public string UpdateType { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Execution that most recently reported this finding.</summary>
    public string? ExecutionId { get; set; }
    /// <summary>Safe preview of the match: TruffleHog's own redacted rendering (never the raw secret). Empty for other findings.</summary>
    public string Preview { get; set; } = "";
    /// <summary>Pull/merge request number Renovate opened for this update, when run in PR mode.</summary>
    public int? PullRequestNumber { get; set; }
    /// <summary>Upstream project URL of an outdated dependency (Renovate's sourceUrl): where its changelog / releases live.</summary>
    public string SourceUrl { get; set; } = "";
    /// <summary>Commit that introduced the secret (git-history scans only); empty for filesystem scans, which only see the checked-out files.</summary>
    public string IntroducedCommit { get; set; } = "";
    /// <summary>Who is working on it (free text: a user name, team or e-mail).</summary>
    public string Assignee { get; set; } = "";
    /// <summary>When the finding was resolved (manually or because a scan no longer reports it); feeds mean-time-to-resolve.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
    /// <summary>Line of the dependency in its manifest (looked up from the forge at the scanned commit); null when unknown.</summary>
    public int? SourceLine { get; set; }
    /// <summary>JSON list of known vulnerabilities (OSV) affecting the dependency's current version; empty when none or lookup is off.</summary>
    public string Advisories { get; set; } = "";
    /// <summary>Severity reported by a generic (SARIF / Aegis JSON) scanner; empty for built-ins, whose severity is derived in <see cref="AegisScan.KubeOps.Scanning.FindingSeverity"/>.</summary>
    public string Severity { get; set; } = "";
    /// <summary>Scanner category (secrets, vulnerabilities, ...), copied from the scanner at scan time.</summary>
    public string Category { get; set; } = "";
    /// <summary>Documentation link for the rule (SARIF helpUri); http(s) only.</summary>
    public string HelpUrl { get; set; } = "";
}

/// <summary>Receives a "sarif.updated" POST (the SARIF of the repository's findings for the scanner that just ran) after each successful scan. Empty RepositoryId / Scanner = all.</summary>
public sealed class WebhookEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>HMAC-SHA256 signing key (X-Aegis-Signature); write-only in the API.</summary>
    public string Secret { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string Scanner { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>A container image linked to a repository by hand; images detected in the repository's files are found by each SBOM scan and not stored here.</summary>
public sealed class RepositoryImageEntity
{
    public string RepositoryId { get; set; } = "";
    public string Ref { get; set; } = "";
}

/// <summary>
/// Latest CycloneDX SBOM of one artifact of a repository: its checkout (Kind "repository") or a linked container image (Kind "image"; <see cref="Ref"/> is the
/// image reference as configured, never the digest). A failed refresh keeps the previous <see cref="Document"/> and sets <see cref="Error"/>.
/// </summary>
public sealed class SbomEntity
{
    public string Id { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string Kind { get; set; } = "repository";
    public string Ref { get; set; } = "";
    /// <summary>"manual" or "detected" for images, empty for the repository.</summary>
    public string Origin { get; set; } = "";
    /// <summary>"registry" when the SBOM was read from the image's registry (OCI referrers), "generated" when Trivy built it from the layers.</summary>
    public string Source { get; set; } = "generated";
    public string CommitSHA { get; set; } = "";
    public string? ExecutionId { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public int ComponentCount { get; set; }
    public int Critical { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    /// <summary>Failure reason of the latest refresh; empty when it succeeded.</summary>
    public string Error { get; set; } = "";
    /// <summary>The CycloneDX JSON document as produced by Trivy (served for download, parsed on demand for the UI).</summary>
    public string Document { get; set; } = "";
}

/// <summary>An acknowledged or snoozed alert. Alert keys are derived from the underlying signal (e.g. "run:&lt;id&gt;"), so a new failure raises a new alert.</summary>
public sealed class AlertAckEntity
{
    public string Key { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTimeOffset AcknowledgedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Null = acknowledged until the signal goes away; otherwise the alert reappears after this time.</summary>
    public DateTimeOffset? SnoozeUntil { get; set; }
}

/// <summary>Value is a JSON-encoded scalar/object so settings can hold numbers, strings, or bools without a schema change.</summary>
public sealed class SettingEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Command/Args are JSON string arrays; `{{workspace}}` in either is substituted with the scan workspace path.</summary>
public sealed class ScannerEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string CommandJson { get; set; } = "[]";
    public string ArgsJson { get; set; } = "[]";
    public bool BuiltIn { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>How the scan log is interpreted: native (built-in parser), sarif, aegis-json, or none (output not interpreted).</summary>
    public string OutputFormat { get; set; } = "none";
    /// <summary>secrets | dependencies | vulnerabilities | code | iac | other — drives wording and auto-resolve semantics.</summary>
    public string Category { get; set; } = "other";
    /// <summary>JSON int array of exit codes that mean "the scan ran" (e.g. [0,1] for scanners that exit 1 on findings).</summary>
    public string SuccessExitCodesJson { get; set; } = "[0]";
}

/// <summary>Maps an IdP group name (as it appears in the token's "groups" claim) to an AegisScan role, in addition to any group already named exactly "admin"/"operator"/"viewer".</summary>
public sealed class RoleMappingEntity
{
    public string IdpGroup { get; set; } = "";
    public string AegisRole { get; set; } = "";
}

public sealed class AuditEntity
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Outcome { get; set; } = "";
    /// <summary>JSON-encoded free-form detail (e.g. changed fields); never secret values.</summary>
    public string Metadata { get; set; } = "{}";
}
