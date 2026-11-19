using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Parses Renovate output run as `--platform=local --dry-run=lookup` with `LOG_FORMAT=json LOG_LEVEL=debug`.
/// The local platform never creates branches, so the report's `branches[]` is always empty; the authoritative
/// result is the debug line `packageFiles with updates` (every extracted dependency with its available
/// `updates[]`). Each dependency with an update becomes a "RenovateDependency" finding. Genuine repository
/// config problems from the `Printing report` line become "RenovateConfig" findings; environment noise
/// (local platform can't resolve presets, no GitHub token) is reported in the run output, not as findings.
/// </summary>
public static partial class RenovateResultParser
{
    private static readonly string[] EnvironmentNoise = ["default owner preset", "GitHub token is required"];
    private static readonly string[] UpdateRank = ["major", "minor", "patch"];

    public static ScanParseResult Parse(string jsonLines, string repositoryId)
    {
        var findings = new Dictionary<string, FindingEntity>();
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var keepOpen = new HashSet<string>();
        var links = new List<RunLink>();
        var awaitingDashboardIssue = false;
        var pullRequestMode = false; // the run talked to a forge (not the read-only local platform), so it is authoritative about pull requests
        var newValues = new Dictionary<string, string>();
        var dependencies = new List<DependencyInfo>();
        var branches = new List<(string PackageFile, string Dep, string Current, string New, int Pr)>();
        var authoritative = false;
        int deps = 0, files = 0, skippedForToken = 0;

        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.TrimStart().StartsWith('{')) continue; // skip non-JSON log noise on the same stream
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var msg = root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (msg is not null && root.TryGetProperty("level", out var level) && level.TryGetInt32(out var lv) && lv >= 40)
                    warnings.Add(WarningText(root, msg));

                if (msg == "packageFiles with updates" && root.TryGetProperty("config", out var config))
                {
                    authoritative = true;
                    foreach (var manager in config.EnumerateObject())
                        foreach (var packageFile in manager.Value.EnumerateArray())
                        {
                            files++;
                            var path = Str(packageFile, "packageFile");
                            if (!packageFile.TryGetProperty("deps", out var depList)) continue;
                            foreach (var dep in depList.EnumerateArray())
                            {
                                deps++;
                                if (Str(dep, "skipReason") == "github-token-required") skippedForToken++;
                                AddUpdate(findings, repositoryId, path, dep, newValues, dependencies);
                                // A skipped dependency (no token, invalid version...) could not be evaluated: keep its existing finding open.
                                if (Str(dep, "skipReason").Length > 0)
                                    keepOpen.Add(Build(repositoryId, "RenovateDependency", path, $"{Str(dep, "depName")}:{Str(dep, "currentValue")}").Fingerprint);
                            }
                        }
                }
                else if (msg == "Printing report" && root.TryGetProperty("report", out var report)
                    && report.TryGetProperty("repositories", out var repositories))
                {
                    foreach (var repository in repositories.EnumerateObject())
                    {
                        AddProblems(findings, repositoryId, repository.Value, warnings);
                        AddPullRequests(repository.Value, links, branches);
                    }
                }
                else if (msg == "ensureIssue(Dependency Dashboard)") { awaitingDashboardIssue = true; pullRequestMode = true; }
                else if (msg == "ensureDependencyDashboard()") pullRequestMode = true;
                else if (awaitingDashboardIssue && msg is not null && DashboardIssue().Match(msg) is { Success: true } issue)
                {
                    // "Created new Issue #N", "Issue #N is open and up to date", "Updating Issue #N"... all follow ensureIssue(...).
                    awaitingDashboardIssue = false;
                    links.Add(new RunLink("issue", "Dependency Dashboard", int.Parse(issue.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }

        // Attach each pull request to the finding for the update it implements (same dependency and target version).
        foreach (var (packageFile, depName, current, newValue, pr) in branches)
        {
            var fingerprint = Build(repositoryId, "RenovateDependency", packageFile, $"{depName}:{current}").Fingerprint;
            if (findings.TryGetValue(fingerprint, out var finding) && newValues.GetValueOrDefault(fingerprint) == newValue) finding.PullRequestNumber = pr;
        }

        var output = new StringBuilder();
        if (authoritative)
            output.AppendLine($"Extracted {deps} dependencies from {files} package file(s); {findings.Values.Count(f => f.Detector == "RenovateDependency")} update(s) available.");
        else
            output.AppendLine("Renovate did not report any dependency data (repository config may be invalid); existing findings were left untouched.");
        if (skippedForToken > 0)
            output.AppendLine($"{skippedForToken} dependency lookup(s) skipped: a GitHub token is required. Set GITHUB_COM_TOKEN via the 'aegisscan-scanner-credentials' Secret (key 'github-token').");
        foreach (var link in links.Where(l => l.Kind == "pull_request")) output.AppendLine($"pull request #{link.Number}: {link.Title}");
        foreach (var link in links.Where(l => l.Kind == "issue")) output.AppendLine($"issue #{link.Number}: {link.Title}");
        foreach (var warning in warnings) output.AppendLine($"warning: {warning}");
        return new ScanParseResult(findings.Values.ToList(), authoritative, ScanOutputSanitizer.Sanitize(output.ToString()), keepOpen, links, pullRequestMode, dependencies);
    }

    private static void AddUpdate(Dictionary<string, FindingEntity> findings, string repositoryId, string packageFile, JsonElement dep, Dictionary<string, string> newValues, List<DependencyInfo> dependencies)
    {
        if (!dep.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array || updates.GetArrayLength() == 0) return;
        var name = Str(dep, "depName");
        var current = Str(dep, "currentValue");
        var best = updates.EnumerateArray()
            .OrderBy(u => Array.IndexOf(UpdateRank, Str(u, "updateType")) is var i and >= 0 ? i : UpdateRank.Length)
            .First();
        // newValue is deliberately not part of the fingerprint: otherwise every upstream release would mint a new finding.
        var finding = Build(repositoryId, "RenovateDependency", packageFile, $"{name}:{current}");
        finding.Detail = $"{name} {current} -> {Str(best, "newValue")}";
        finding.UpdateType = Str(best, "updateType");
        finding.SourceUrl = Str(dep, "sourceUrl");
        if (findings.TryAdd(finding.Fingerprint, finding))
        {
            newValues[finding.Fingerprint] = Str(best, "newValue");
            // Prefer the resolved version; a range like ^4.17.1 is reduced to its base version.
            var version = Str(dep, "currentVersion"); if (version.Length == 0) version = current.TrimStart('^', '~', '=', 'v', ' ', '>', '<');
            dependencies.Add(new DependencyInfo(finding.Fingerprint, packageFile, name, Str(dep, "datasource"), version));
        }
    }

    /// <summary>In PR mode the report lists one branch per pull request (`prNo`, `prTitle`) with the upgrades it contains.</summary>
    private static void AddPullRequests(JsonElement repositoryReport, List<RunLink> links, List<(string, string, string, string, int)> branches)
    {
        if (!repositoryReport.TryGetProperty("branches", out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var branch in list.EnumerateArray())
        {
            if (!branch.TryGetProperty("prNo", out var no) || !no.TryGetInt32(out var pr) || pr <= 0) continue;
            links.Add(new RunLink("pull_request", Str(branch, "prTitle", Str(branch, "branchName")), pr));
            if (!branch.TryGetProperty("upgrades", out var upgrades) || upgrades.ValueKind != JsonValueKind.Array) continue;
            foreach (var upgrade in upgrades.EnumerateArray())
                branches.Add((Str(upgrade, "packageFile"), Str(upgrade, "depName"), Str(upgrade, "currentValue"), Str(upgrade, "newValue"), pr));
        }
    }

    private static void AddProblems(Dictionary<string, FindingEntity> findings, string repositoryId, JsonElement repositoryReport, SortedSet<string> warnings)
    {
        if (!repositoryReport.TryGetProperty("problems", out var problems) || problems.ValueKind != JsonValueKind.Array) return;
        foreach (var problem in problems.EnumerateArray())
        {
            var message = Str(problem, "msg", "Repository has invalid config");
            var detail = problem.TryGetProperty("err", out var err) ? Str(err, "validationError", Str(err, "message")) : "";
            // Only repository config validation errors are findings; everything else in `problems` (missing tokens, release-notes
            // lookups, local-platform limits) is environment noise surfaced as a warning in the run output.
            var isConfigProblem = message.Contains("invalid config", StringComparison.OrdinalIgnoreCase)
                || (problem.TryGetProperty("err", out var e) && Str(e, "message") == "config-validation");
            if (!isConfigProblem || EnvironmentNoise.Any(n => message.Contains(n, StringComparison.Ordinal)))
            {
                warnings.Add(message);
                continue;
            }
            var finding = Build(repositoryId, "RenovateConfig", "renovate.json", $"{message}:{detail}");
            finding.Detail = detail.Length > 0 ? $"{message}: {detail}" : message;
            findings.TryAdd(finding.Fingerprint, finding);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Issue #(\d+)")]
    private static partial System.Text.RegularExpressions.Regex DashboardIssue();

    private static string WarningText(JsonElement line, string msg)
    {
        var detail = line.TryGetProperty("err", out var err) ? Str(err, "validationError", Str(err, "message")) : "";
        return detail.Length > 0 ? $"{msg}: {detail}" : msg;
    }

    private static FindingEntity Build(string repositoryId, string detector, string path, string identity) => new()
    {
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{repositoryId}:{detector}:{path}:{identity}"))),
        RepositoryId = repositoryId,
        Scanner = "renovate",
        Detector = detector,
        SafePath = path,
        State = "open"
    };

    private static string Str(JsonElement element, string property, string fallback = "") =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
}
