namespace AegisScan.KubeOps.Api;

public sealed record RepositoryView(string Id, string Name, string Provider, string CloneUrl, string DefaultBranch, string Status, string CacheState)
{
    public int ActiveFindingCount { get; init; }
    public string LatestCommitSHA { get; init; } = "";
    public Dictionary<string, long>? Languages { get; init; }
}
public sealed record ExecutionView(string Id, string RepositoryId, string Scanner, string Mode, string Status, int Attempt, string SanitizedLog)
{
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}
public sealed record FindingView(string Fingerprint, string RepositoryId, string Detector, bool Verified, string SafePath, string CommitSHA, string State);
public sealed record AuditView(long Id, DateTimeOffset Timestamp, string Actor, string Action, string Target, string Outcome, string Metadata);
public sealed record ScannerView(string Id, string Name, string Image, List<string> Command, List<string> Args, bool BuiltIn, bool Enabled);
public sealed record RoleMappingView(string IdpGroup, string AegisRole);
