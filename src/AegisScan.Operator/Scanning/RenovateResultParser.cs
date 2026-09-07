using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Parses Renovate's `--report-type=logging` output (requires `LOG_FORMAT=json` — see
/// ScanJobFactory — so the report line is itself parseable JSON, matching TruffleHog's one-
/// object-per-line stream). Looks for the single log line with `msg == "Printing report"` and
/// reads its `report.repositories.*` entries: a repository-level config `problems[]` entry becomes
/// a "RenovateConfig" finding; each proposed dependency update under `branches[].upgrades[]`
/// becomes a "RenovateDependency" finding.
/// </summary>
public static class RenovateResultParser
{
    public static List<FindingEntity> Parse(string jsonLines, string repositoryId)
    {
        var findings = new List<FindingEntity>();
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.TrimStart().StartsWith('{')) continue; // skip non-JSON log noise on the same stream
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("msg", out var msg) || msg.GetString() != "Printing report") continue;
                if (!root.TryGetProperty("report", out var report)) continue;
                if (!report.TryGetProperty("repositories", out var repositories)) continue;

                foreach (var repository in repositories.EnumerateObject())
                {
                    ParseProblems(repository.Value, repositoryId, findings);
                    ParseUpgrades(repository.Value, repositoryId, findings);
                }
            }
        }
        return findings;
    }

    private static void ParseProblems(JsonElement repositoryReport, string repositoryId, List<FindingEntity> findings)
    {
        if (!repositoryReport.TryGetProperty("problems", out var problems) || problems.ValueKind != JsonValueKind.Array) return;
        foreach (var problem in problems.EnumerateArray())
        {
            var message = problem.TryGetProperty("msg", out var m) ? m.GetString() ?? "Repository has invalid config" : "Repository has invalid config";
            var detail = problem.TryGetProperty("err", out var err) && err.TryGetProperty("validationError", out var ve)
                ? ve.GetString() ?? ""
                : "";
            var safePath = detail.Length > 0 ? $"renovate.json: {message}: {detail}" : $"renovate.json: {message}";
            findings.Add(BuildFinding(repositoryId, "RenovateConfig", safePath));
        }
    }

    private static void ParseUpgrades(JsonElement repositoryReport, string repositoryId, List<FindingEntity> findings)
    {
        if (!repositoryReport.TryGetProperty("branches", out var branches) || branches.ValueKind != JsonValueKind.Array) return;
        foreach (var branch in branches.EnumerateArray())
        {
            if (!branch.TryGetProperty("upgrades", out var upgrades) || upgrades.ValueKind != JsonValueKind.Array) continue;
            foreach (var upgrade in upgrades.EnumerateArray())
            {
                var depName = upgrade.TryGetProperty("depName", out var d) ? d.GetString() ?? "dependency" : "dependency";
                var packageFile = upgrade.TryGetProperty("packageFile", out var pf) ? pf.GetString() ?? "" : "";
                var currentVersion = upgrade.TryGetProperty("currentValue", out var cv) ? cv.GetString() ?? "" : "";
                var newVersion = upgrade.TryGetProperty("newValue", out var nv) ? nv.GetString() ?? "" : "";
                var safePath = $"{packageFile}: {depName} {currentVersion} -> {newVersion}";
                findings.Add(BuildFinding(repositoryId, "RenovateDependency", safePath));
            }
        }
    }

    private static FindingEntity BuildFinding(string repositoryId, string detector, string safePath)
    {
        var fingerprintSource = $"{repositoryId}:{detector}:{safePath}";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
        return new FindingEntity
        {
            Fingerprint = fingerprint,
            RepositoryId = repositoryId,
            Detector = detector,
            Verified = false,
            SafePath = safePath,
            CommitSHA = "",
            State = "open"
        };
    }
}
