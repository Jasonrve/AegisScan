using System.Text.RegularExpressions;
using AegisScan.KubeOps.Data;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Outcome of parsing one scanner run. <see cref="Authoritative"/> is true only when the output proves the
/// scanner saw the whole repository — only then may findings it no longer reports be auto-resolved
/// (a Renovate run that aborts on a config error exits 0 with zero findings and must not close anything).
/// <see cref="Output"/> is the human-readable, secret-free log persisted for the run. <see cref="KeepOpen"/> lists fingerprints the
/// scanner could not evaluate this time (e.g. Renovate lookups skipped for lack of a token): they are not reported, but must not be
/// treated as fixed either.
/// </summary>
public sealed record ScanParseResult(List<FindingEntity> Findings, bool Authoritative, string Output, IReadOnlySet<string>? KeepOpen = null, IReadOnlyList<RunLink>? Links = null, bool PullRequestMode = false, IReadOnlyList<DependencyInfo>? Dependencies = null)
{
    /// <summary>Set when the output could not be interpreted: the run must fail with this (sanitized) reason instead of reporting "0 findings".</summary>
    public string? Error { get; init; }
    /// <summary>True for scanners whose output Aegis does not interpret (format "none"): the run shows "not interpreted", never a finding count.</summary>
    public bool NotInterpreted { get; init; }
    /// <summary>SBOMs produced by the "sbom" scanner (one per repository checkout / container image); persisted by <see cref="SbomStore"/>.</summary>
    public IReadOnlyList<Data.SbomEntity>? Sboms { get; init; }
}

/// <summary>A dependency with an available update: what the enricher needs to locate it in its manifest and look up vulnerabilities.</summary>
public sealed record DependencyInfo(string Fingerprint, string PackageFile, string Name, string Datasource, string Version);

/// <summary>A pull request ("pull_request") or issue ("issue") a scan created or updated on the forge; the URL is derived from the repository when served.</summary>
public sealed record RunLink(string Kind, string Title, int Number);

public static partial class ScanOutputSanitizer
{
    public const int MaxLength = 64 * 1024;

    /// <summary>Strips credentials embedded in URLs and caps the size so a multi-MB debug log never lands in Postgres.</summary>
    public static string Sanitize(string text)
    {
        text = UrlCredentials().Replace(text, "://***@");
        return text.Length <= MaxLength ? text : text[..MaxLength] + "\n… output truncated …";
    }

    [GeneratedRegex(@"://[^/\s@]+@")]
    private static partial Regex UrlCredentials();
}

/// <summary>Single source of truth for finding severity (returned by the API; the UI never derives its own).</summary>
public static class FindingSeverity
{
    public static string For(FindingEntity finding) => finding.Severity.Length > 0 ? finding.Severity : finding.Detector switch
    {
        // A dependency whose *current* version has a known vulnerability is a security finding, whatever the size of the update.
        "RenovateDependency" => finding.Advisories.Length > 2 ? "high" : finding.UpdateType == "major" ? "medium" : "low",
        "RenovateConfig" => "low",
        _ when finding.Verified => "critical",
        "PrivateKey" => "high",
        _ => "medium"
    };
}
