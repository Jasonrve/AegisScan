using AegisScan.KubeOps.Api;
using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class GenericResultParserTests
{
    private static readonly ScannerEntity Trivy = new() { Id = "trivy", Category = "vulnerabilities", OutputFormat = "sarif" };

    // Trimmed shape of `trivy fs --format sarif` / semgrep --sarif output.
    private const string Sarif = """
        {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"Trivy","rules":[
          {"id":"CVE-2024-1","name":"OsPackageVulnerability","shortDescription":{"text":"lodash prototype pollution"},"helpUri":"https://avd.aquasec.com/nvd/cve-2024-1","properties":{"security-severity":"9.8"}},
          {"id":"DS002","shortDescription":{"text":"Image user should not be root"},"helpUri":"javascript:alert(1)","defaultConfiguration":{"level":"warning"}}]}},
          "invocations":[{"executionSuccessful":true}],
          "results":[
            {"ruleId":"CVE-2024-1","ruleIndex":0,"level":"error","message":{"text":"Package lodash 4.17.0 is vulnerable"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"file:///workspace/repo/package-lock.json"},"region":{"startLine":12,"snippet":{"text":"AKIAIOSFODNN7EXAMPLE secret"}}}}]},
            {"ruleId":"DS002","ruleIndex":1,"message":{"text":"Specify a USER"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"Dockerfile"},"region":{"startLine":1}}}]}]}]}
        """;

    [Fact]
    public void ParsesSarifWithSeverityPathAndMaskedPreview()
    {
        var result = GenericResultParser.Parse("sarif", "trivy: scanning...\n" + Sarif + "\ntrailing noise", "repo", Trivy);

        Assert.Null(result.Error);
        Assert.True(result.Authoritative);
        Assert.Equal(2, result.Findings.Count);
        var cve = Assert.Single(result.Findings, f => f.Detector == "CVE-2024-1");
        Assert.Equal("critical", cve.Severity);
        Assert.Equal("package-lock.json", cve.SafePath);
        Assert.Equal(12, cve.SourceLine);
        Assert.Equal("vulnerabilities", cve.Category);
        Assert.Equal("https://avd.aquasec.com/nvd/cve-2024-1", cve.HelpUrl);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", cve.Preview, StringComparison.Ordinal);
        var rule = Assert.Single(result.Findings, f => f.Detector == "DS002");
        Assert.Equal("medium", rule.Severity); // no security-severity: falls back to the rule's default level
        Assert.Equal("", rule.HelpUrl);        // javascript: URLs are dropped
    }

    [Fact]
    public void FingerprintsAreStableAcrossRuns()
    {
        var a = GenericResultParser.Parse("sarif", Sarif, "repo", Trivy).Findings.Select(f => f.Fingerprint).Order().ToList();
        var b = GenericResultParser.Parse("sarif", Sarif, "repo", Trivy).Findings.Select(f => f.Fingerprint).Order().ToList();
        Assert.Equal(a, b);
        Assert.NotEqual(a, GenericResultParser.Parse("sarif", Sarif, "other", Trivy).Findings.Select(f => f.Fingerprint).Order().ToList());
    }

    [Fact]
    public void SecretsWithoutAnExplicitSeverityAreHigh()
    {
        const string gitleaks = """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"gitleaks"}},"results":[{"ruleId":"aws-key","message":{"text":"leak"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"a.env"},"region":{"startLine":1}}}]}]}]}""";
        Assert.Equal("high", Assert.Single(GenericResultParser.Parse("sarif", gitleaks, "repo", new ScannerEntity { Id = "gitleaks", Category = "secrets" }).Findings).Severity);
        Assert.Equal("medium", Assert.Single(GenericResultParser.Parse("sarif", gitleaks, "repo", new ScannerEntity { Id = "x", Category = "code" }).Findings).Severity);
    }

    [Fact]
    public void FingerprintsSurviveShiftedLinesAndChangedMessages()
    {
        static string Doc(int line, string message) => """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"t"}},"results":[{"ruleId":"CVE-1","message":{"text":"MSG"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"a.lock"},"region":{"startLine":LINE}}}]}]}]}""".Replace("MSG", message).Replace("LINE", line.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var before = GenericResultParser.Parse("sarif", Doc(3, "fixed in 1.2"), "repo", Trivy).Findings.Single().Fingerprint;
        var after = GenericResultParser.Parse("sarif", Doc(30, "fixed in 1.3 (updated advisory)"), "repo", Trivy).Findings.Single().Fingerprint;
        Assert.Equal(before, after);
    }

    [Fact]
    public void SameRuleInDifferentFilesWithSharedPartialFingerprintStaysDistinct()
    {
        static string Hit(string file) => """{"ruleId":"R","message":{"text":"m"},"partialFingerprints":{"commitSha":"abc"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"FILE"}}}]}""".Replace("FILE", file);
        var doc = """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"t"}},"results":[""" + Hit("a.py") + "," + Hit("b.py") + "," + Hit("b.py") + "]}]}";
        Assert.Equal(3, GenericResultParser.Parse("sarif", doc, "repo", Trivy).Findings.Select(f => f.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void SecretMessagesAreNotStored()
    {
        const string doc = """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"t","rules":[{"id":"tok","shortDescription":{"text":"API token"}}]}},"results":[{"ruleId":"tok","message":{"text":"found ghp_SUPERSECRET in file"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"a.env"}}}]}]}]}""";
        var f = Assert.Single(GenericResultParser.Parse("sarif", doc, "repo", new ScannerEntity { Id = "s", Category = "secrets" }).Findings);
        Assert.DoesNotContain("SUPERSECRET", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedInvocationIsNotAuthoritative()
    {
        var result = GenericResultParser.Parse("sarif", Sarif.Replace("\"executionSuccessful\":true", "\"executionSuccessful\":false"), "repo", Trivy);
        Assert.False(result.Authoritative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"runs\": [")]
    public void UnparseableOutputIsAnErrorNotZeroFindings(string log)
    {
        var result = GenericResultParser.Parse("sarif", log, "repo", Trivy);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void ParsesAegisJsonBetweenFences()
    {
        var log = "starting\nAEGIS-RESULT-BEGIN\n{\"aegisVersion\":1,\"authoritative\":true,\"findings\":[{\"id\":\"todo-1\",\"title\":\"Hardcoded password\",\"severity\":\"high\",\"path\":\"src/app.py\",\"line\":7,\"url\":\"https://example.com/r\",\"preview\":\"password = 'hunter2'\"}]}\nAEGIS-RESULT-END\n";
        var result = GenericResultParser.Parse("aegis-json", log, "repo", new ScannerEntity { Id = "demo", Category = "code" });

        Assert.Null(result.Error);
        Assert.True(result.Authoritative);
        var f = Assert.Single(result.Findings);
        Assert.Equal("high", f.Severity);
        Assert.Equal("src/app.py", f.SafePath);
        Assert.Equal(7, f.SourceLine);
        Assert.DoesNotContain("hunter2", f.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void NoneFormatIsReportedAsNotInterpreted()
    {
        var result = GenericResultParser.Parse("none", "whatever", "repo", new ScannerEntity { Id = "x" });
        Assert.True(result.NotInterpreted);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("file:///workspace/repo/a/b.py", "a/b.py")]
    [InlineData("./x/../y.py", "x/y.py")]
    [InlineData("/etc/passwd", "etc/passwd")]
    public void PathsAreRepositoryRelative(string uri, string expected) => Assert.Equal(expected, GenericResultParser.CleanPath(uri));

    [Fact]
    public void ValidatesTheOutputContract()
    {
        Assert.Null(ApiEndpoints.ValidateOutputContract(new ApiEndpoints.ScannerInput("a", "A", "img", null, null, null, "sarif", "code", [0, 1])));
        Assert.NotNull(ApiEndpoints.ValidateOutputContract(new ApiEndpoints.ScannerInput("a", "A", "img", null, null, null, "xml")));
        Assert.NotNull(ApiEndpoints.ValidateOutputContract(new ApiEndpoints.ScannerInput("a", "A", "img", null, null, null, null, "nope")));
        Assert.NotNull(ApiEndpoints.ValidateOutputContract(new ApiEndpoints.ScannerInput("a", "A", "img", null, null, null, null, null, [256])));
    }
}
