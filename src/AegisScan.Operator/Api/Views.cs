namespace AegisScan.KubeOps.Api;

public sealed record RepositoryView(string Id, string Name, string Provider, string CloneUrl, string DefaultBranch, string Status, string CacheState)
{
    /// <summary>Operational health derived from the latest finished scan: blocked | failing | never-scanned | healthy (Status keeps its own semantics).</summary>
    public string Health { get; init; } = "healthy";
    public int ActiveFindingCount { get; init; }
    public string LatestCommitSHA { get; init; } = "";
    public Dictionary<string, long>? Languages { get; init; }
    /// <summary>Browser URL of the repository on its forge (after the forgeUrlOverrides mapping).</summary>
    public string? WebUrl { get; init; }
}
public sealed record ExecutionView(string Id, string RepositoryId, string Scanner, string Mode, string Status, int Attempt, string SanitizedLog)
{
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int FindingCount { get; init; }
    /// <summary>False when the scanner's output is not interpreted: FindingCount is then meaningless.</summary>
    public bool OutputInterpreted { get; init; } = true;
    public string CommitSHA { get; init; } = "";
    public string? CommitUrl { get; init; }
    public string? RepositoryUrl { get; init; }
    /// <summary>First error line of a failed run (e.g. the clone error), so the cause is visible without opening the output.</summary>
    public string? FailureReason { get; init; }
    /// <summary>Pull requests / issues this scan opened or updated.</summary>
    public List<LinkView> Links { get; init; } = [];
}
public sealed record LinkView(string Kind, string Title, string Url);
public sealed record FindingView(string Fingerprint, string RepositoryId, string Detector, bool Verified, string SafePath, string CommitSHA, string State)
{
    public string Scanner { get; init; } = "";
    public string Category { get; init; } = "";
    /// <summary>Documentation link of the rule (SARIF helpUri), http(s) only.</summary>
    public string HelpUrl { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Detail { get; init; } = "";
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public string? ExecutionId { get; init; }
    /// <summary>TruffleHog's own redacted rendering of the match; empty for other findings.</summary>
    public string Preview { get; init; } = "";
    public string? FileUrl { get; init; }
    public string? CommitUrl { get; init; }
    public string? PullRequestUrl { get; init; }
    public int? PullRequestNumber { get; init; }
    public string? ChangelogUrl { get; init; }
    /// <summary>Known vulnerabilities of the dependency's current version (OSV); empty when none or the lookup is off.</summary>
    public List<AdvisoryView> Advisories { get; init; } = [];
    public int? SourceLine { get; init; }
    /// <summary>Commit that introduced the secret (git-history scans); the file link points at this revision.</summary>
    public string IntroducedCommit { get; init; } = "";
    public string? IntroducedCommitUrl { get; init; }
    public string Assignee { get; init; } = "";
    /// <summary>First seen + the SLA for its severity (setting "slaDays").</summary>
    public DateTimeOffset DueAt { get; init; }
    public bool Overdue { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
}
public sealed record AdvisoryView(string Id, string Summary, string Url);
public sealed record FindingHistoryView(DateTimeOffset Timestamp, string Actor, string From, string To, string Reason);
public sealed record AuditView(long Id, DateTimeOffset Timestamp, string Actor, string Action, string Target, string Outcome, string Metadata);
public sealed record ScannerView(string Id, string Name, string Image, List<string> Command, List<string> Args, bool BuiltIn, bool Enabled, string OutputFormat, string Category, List<int> SuccessExitCodes);
public sealed record RoleMappingView(string IdpGroup, string AegisRole);
