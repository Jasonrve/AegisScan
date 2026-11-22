using System.Text.Json.Nodes;
using AegisScan.KubeOps.Data;

namespace AegisScan.KubeOps.Scanning;

/// <summary>Builds a SARIF 2.1.0 log of a repository's findings, one run per scanner. Never includes <see cref="FindingEntity.Preview"/>.</summary>
public static class SarifExporter
{
    public static JsonObject Build(string repositoryId, IEnumerable<FindingEntity> findings)
    {
        var runs = new JsonArray();
        foreach (var group in findings.GroupBy(f => f.Scanner).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rules = new JsonArray();
            foreach (var rule in group.GroupBy(f => f.Detector).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var r = new JsonObject { ["id"] = rule.Key };
                if (rule.Select(f => f.HelpUrl).FirstOrDefault(u => u.Length > 0) is { } help) r["helpUri"] = help;
                rules.Add(r);
            }
            var results = new JsonArray();
            foreach (var f in group.OrderBy(f => f.Fingerprint, StringComparer.Ordinal))
            {
                var severity = FindingSeverity.For(f);
                var result = new JsonObject
                {
                    ["ruleId"] = f.Detector,
                    ["level"] = severity is "critical" or "high" ? "error" : severity == "medium" ? "warning" : "note",
                    ["message"] = new JsonObject { ["text"] = f.Detail.Length > 0 ? $"{f.Detector}: {f.Detail}" : f.Detector },
                    ["partialFingerprints"] = new JsonObject { ["aegis/v1"] = f.Fingerprint },
                    ["properties"] = new JsonObject { ["severity"] = severity, ["category"] = f.Category, ["state"] = f.State, ["verified"] = f.Verified, ["commit"] = f.CommitSHA, ["firstSeen"] = f.FirstSeenAt, ["lastSeen"] = f.LastSeenAt }
                };
                if (f.SafePath.Length > 0)
                {
                    var physical = new JsonObject { ["artifactLocation"] = new JsonObject { ["uri"] = f.SafePath } };
                    if (f.SourceLine is > 0) physical["region"] = new JsonObject { ["startLine"] = f.SourceLine };
                    result["locations"] = new JsonArray(new JsonObject { ["physicalLocation"] = physical });
                }
                // Resolved / ignored findings stay in the log (complete list) but consumers can tell they are not active.
                if (f.State != "open") result["suppressions"] = new JsonArray(new JsonObject { ["kind"] = "external", ["justification"] = f.State });
                results.Add(result);
            }
            runs.Add(new JsonObject
            {
                ["tool"] = new JsonObject { ["driver"] = new JsonObject { ["name"] = group.Key, ["rules"] = rules } },
                ["automationDetails"] = new JsonObject { ["id"] = $"aegisscan/{repositoryId}/{group.Key}" },
                ["results"] = results
            });
        }
        return new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = runs
        };
    }
}
