using AegisScan.KubeOps.Scanning;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class RenovateResultParserTests
{
    // Captured verbatim from a real `renovate --platform=local --report-type=logging` run
    // (with LOG_FORMAT=json) against a repository whose renovate.json references an
    // unresolvable preset.
    private const string ConfigProblemReportLine =
        """{"name":"renovate","hostname":"test","pid":1,"level":30,"logContext":"x","report":{"problems":[],"repositories":{"local":{"problems":[{"name":"renovate","level":40,"logContext":"x","err":{"validationError":"Preset caused unexpected error (local>rinchsan/renovate-config)","message":"config-validation"},"msg":"Repository has invalid config"}],"branches":[],"packageFiles":{}}}},"msg":"Printing report","time":"2026-01-01T00:00:00.000Z","v":0}""";

    [Fact]
    public void ParsesRepositoryConfigProblemsIntoFindings()
    {
        var findings = RenovateResultParser.Parse(ConfigProblemReportLine, "renovate-config-validator");

        var finding = Assert.Single(findings);
        Assert.Equal("renovate-config-validator", finding.RepositoryId);
        Assert.Equal("RenovateConfig", finding.Detector);
        Assert.False(finding.Verified);
        Assert.Contains("Preset caused unexpected error", finding.SafePath);
    }

    [Fact]
    public void ParsesDependencyUpgradesIntoFindings()
    {
        // Shape per Renovate's documented report schema (branches[].upgrades[]); not captured from
        // a live run since no available test repository produces a fully-resolved packageFiles set.
        const string upgradeReportLine =
            """{"name":"renovate","hostname":"test","pid":1,"level":30,"logContext":"x","report":{"problems":[],"repositories":{"local":{"problems":[],"branches":[{"branchName":"renovate/lodash-4.x","upgrades":[{"depName":"lodash","packageFile":"package.json","currentValue":"4.17.20","newValue":"4.17.21"}]}],"packageFiles":{}}}},"msg":"Printing report","time":"2026-01-01T00:00:00.000Z","v":0}""";

        var findings = RenovateResultParser.Parse(upgradeReportLine, "renovate-config-validator");

        var finding = Assert.Single(findings);
        Assert.Equal("RenovateDependency", finding.Detector);
        Assert.Equal("package.json: lodash 4.17.20 -> 4.17.21", finding.SafePath);
    }

    [Fact]
    public void IgnoresNonReportLogLines()
    {
        var findings = RenovateResultParser.Parse(
            """{"level":30,"msg":"Repository started","time":"2026-01-01T00:00:00.000Z"}""",
            "renovate-config-validator");

        Assert.Empty(findings);
    }
}
