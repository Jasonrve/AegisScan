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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FindingEntity>().HasKey(f => f.Fingerprint);
        modelBuilder.Entity<SettingEntity>().HasKey(s => s.Key);
        modelBuilder.Entity<AuditEntity>().HasIndex(a => a.Timestamp);
        modelBuilder.Entity<RoleMappingEntity>().HasKey(m => m.IdpGroup);

        modelBuilder.Entity<RepositoryEntity>().HasData(
            new RepositoryEntity { Id = "acme-web", Name = "acme-web", Provider = "github", CloneUrl = "https://github.com/acme/acme-web.git", DefaultBranch = "main", Status = "Ready", CacheState = "warm" },
            new RepositoryEntity { Id = "platform-api", Name = "platform-api", Provider = "gitlab", CloneUrl = "https://gitlab.com/acme/platform-api.git", DefaultBranch = "main", Status = "Ready", CacheState = "warm" },
            new RepositoryEntity { Id = "mobile-app", Name = "mobile-app", Provider = "github", CloneUrl = "https://github.com/acme/mobile-app.git", DefaultBranch = "main", Status = "Ready", CacheState = "stale" });

        modelBuilder.Entity<SettingEntity>().HasData(
            new SettingEntity { Key = "retentionDays", Value = "90" },
            new SettingEntity { Key = "cacheRetentionGB", Value = "20" });

        modelBuilder.Entity<ScannerEntity>().HasData(
            new ScannerEntity { Id = "trufflehog", Name = "TruffleHog", Image = "trufflesecurity/trufflehog:3.90.8", CommandJson = "[\"trufflehog\",\"filesystem\",\"{{workspace}}\",\"--json\",\"--no-update\"]", ArgsJson = "[]", BuiltIn = true, Enabled = true },
            new ScannerEntity { Id = "renovate", Name = "Renovate", Image = "renovate/renovate:latest", CommandJson = "[]", ArgsJson = "[\"--platform=local\",\"--report-type=logging\"]", BuiltIn = true, Enabled = true });
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
