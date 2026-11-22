using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class SarifExporterTests
{
    [Fact]
    public void GroupsByScannerAndMarksNonOpenFindingsSuppressed()
    {
        var sarif = SarifExporter.Build("team/repo",
        [
            new FindingEntity { Fingerprint = "a", Scanner = "trufflehog", Detector = "AWS", Verified = true, SafePath = "src/a.cs", State = "open" },
            new FindingEntity { Fingerprint = "b", Scanner = "renovate", Detector = "RenovateDependency", Detail = "x 1 -> 2", SafePath = "package.json", SourceLine = 7, State = "resolved" }
        ]);

        Assert.Equal("2.1.0", (string?)sarif["version"]);
        var runs = sarif["runs"]!.AsArray();
        Assert.Equal(2, runs.Count);
        var secret = runs.Single(r => (string?)r!["tool"]!["driver"]!["name"] == "trufflehog")!["results"]![0]!;
        Assert.Equal("error", (string?)secret["level"]); // verified secret = critical
        Assert.Null(secret["suppressions"]);
        var dep = runs.Single(r => (string?)r!["tool"]!["driver"]!["name"] == "renovate")!["results"]![0]!;
        Assert.Equal(7, (int?)dep["locations"]![0]!["physicalLocation"]!["region"]!["startLine"]);
        Assert.NotNull(dep["suppressions"]);
    }
}
