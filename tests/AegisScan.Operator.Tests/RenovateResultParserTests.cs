using Microsoft.EntityFrameworkCore;
using AegisScan.KubeOps.Api;
using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class RenovateResultParserTests
{
    // Shapes captured from a real `renovate --platform=local --dry-run=lookup` run (LOG_FORMAT=json LOG_LEVEL=debug)
    // against expressjs/express, trimmed to a few dependencies.
    private const string PackageFilesLine =
        """{"level":20,"msg":"packageFiles with updates","config":{"npm":[{"packageFile":"package.json","deps":[{"depName":"eslint","currentValue":"8.47.0","depType":"devDependencies","updates":[{"newValue":"8.57.1","updateType":"minor"},{"newValue":"10.12.0","updateType":"major"}]},{"depName":"cookie","currentValue":"^0.7.1","updates":[{"newValue":"^2.0.0","updateType":"major"}]},{"depName":"debug","currentValue":"^4.4.0","updates":[]}]}],"github-actions":[{"packageFile":".github/workflows/ci.yml","deps":[{"depName":"actions/checkout","currentValue":"v7.0.1","skipReason":"github-token-required","updates":[]}]}]}}""";

    private const string ConfigProblemReportLine =
        """{"level":30,"report":{"problems":[],"repositories":{"local":{"problems":[{"name":"renovate","level":40,"err":{"validationError":"Preset caused unexpected error (local>rinchsan/renovate-config)","message":"config-validation"},"msg":"Repository has invalid config"},{"name":"renovate","level":40,"err":{"message":"The platform you're using (local) does not support local presets."},"msg":"Unknown error fetching default owner preset"}]}}},"msg":"Printing report"}""";

    [Fact]
    public void ReportsOneFindingPerDependencyWithAnUpdate()
    {
        var result = RenovateResultParser.Parse(PackageFilesLine, "express");

        Assert.True(result.Authoritative);
        Assert.Equal(2, result.Findings.Count);
        var eslint = Assert.Single(result.Findings, f => f.Detail.StartsWith("eslint", StringComparison.Ordinal));
        Assert.Equal("RenovateDependency", eslint.Detector);
        Assert.Equal("package.json", eslint.SafePath);
        Assert.Equal("renovate", eslint.Scanner);
        // The most significant update wins when several are available.
        Assert.Equal("eslint 8.47.0 -> 10.12.0", eslint.Detail);
        Assert.Equal("major", eslint.UpdateType);
    }

    [Fact]
    public void FingerprintIgnoresTheNewVersion()
    {
        var first = RenovateResultParser.Parse(PackageFilesLine, "express").Findings.First(f => f.Detail.StartsWith("cookie", StringComparison.Ordinal));
        var later = RenovateResultParser.Parse(PackageFilesLine.Replace("^2.0.0", "^2.1.0"), "express").Findings.First(f => f.Detail.StartsWith("cookie", StringComparison.Ordinal));

        Assert.Equal(first.Fingerprint, later.Fingerprint);
    }

    [Fact]
    public void WarnsAboutTokenSkipsInsteadOfCreatingFindings()
    {
        var result = RenovateResultParser.Parse(PackageFilesLine, "express");

        Assert.Contains("GITHUB_COM_TOKEN", result.Output);
        Assert.DoesNotContain(result.Findings, f => f.Detail.Contains("actions/checkout"));
    }

    [Fact]
    public void RepositoryConfigProblemsBecomeFindingsButEnvironmentNoiseDoesNot()
    {
        var result = RenovateResultParser.Parse(ConfigProblemReportLine, "renovate-config-validator");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("RenovateConfig", finding.Detector);
        Assert.Contains("Preset caused unexpected error", finding.Detail);
    }

    [Fact]
    public void SkippedDependenciesAreKeptOpenNotTreatedAsFixed()
    {
        // An expired GitHub token makes lookups skip: an existing finding for that dependency must not be auto-resolved.
        var line = """{"level":20,"msg":"packageFiles with updates","config":{"npm":[{"packageFile":"package.json","deps":[{"depName":"lodash","currentValue":"4.0.0","skipReason":"github-token-required","updates":[]}]}]}}""";

        var result = RenovateResultParser.Parse(line, "r");

        Assert.Empty(result.Findings);
        Assert.Single(result.KeepOpen!);
    }

    [Fact]
    public void AbortedRunIsNotAuthoritative()
    {
        // No `packageFiles with updates` line: the run died on config validation, so it must not close existing findings.
        Assert.False(RenovateResultParser.Parse(ConfigProblemReportLine, "r").Authoritative);
    }

    [Fact]
    public void IgnoresUnrelatedLogLines()
    {
        var result = RenovateResultParser.Parse("""{"level":30,"msg":"Repository started"}""", "r");

        Assert.Empty(result.Findings);
        Assert.False(result.Authoritative);
    }
}

public sealed class TruffleHogResultParserTests
{
    private const string FakeSecret = "AKIAFAKEFAKEFAKE1234";

    private static readonly string FindingLine =
        """{"SourceMetadata":{"Data":{"Filesystem":{"file":"/workspace/repo/new_key","line":3}}},"DetectorName":"AWS","Verified":true,"Raw":"AKIAFAKEFAKEFAKE1234","RawV2":"AKIAFAKEFAKEFAKE1234:secret","Redacted":"AKIAFAKE"}""";

    private const string FinishedLine = """{"level":"info-0","msg":"finished scanning","chunks":31,"verified_secrets":2,"unverified_secrets":1}""";

    [Fact]
    public void EmptyOrTruncatedLogIsNeverAuthoritative()
    {
        // A reaped pod or rotated log yields "", which must not be read as "all findings fixed".
        Assert.False(TruffleHogResultParser.Parse("", "r").Authoritative);
        Assert.False(TruffleHogResultParser.Parse(FindingLine, "r").Authoritative);
    }

    [Fact]
    public void ParsesFindingWithRepositoryRelativePathAndLine()
    {
        var result = TruffleHogResultParser.Parse(FindingLine + "\n" + FinishedLine, "test-keys");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("new_key", finding.SafePath);
        Assert.Equal("line 3", finding.Detail);
        Assert.True(finding.Verified);
        Assert.Equal("trufflehog", finding.Scanner);
        Assert.True(result.Authoritative);
    }

    [Fact]
    public void PersistedOutputNeverContainsTheSecret()
    {
        var result = TruffleHogResultParser.Parse(FindingLine, "test-keys");

        Assert.DoesNotContain(FakeSecret, result.Output);
        Assert.DoesNotContain("Raw", result.Output);
        Assert.Contains("AWS (verified) in new_key:3", result.Output);
    }

    [Fact]
    public void FingerprintStaysStableAcrossPathDisplayChange()
    {
        // Findings stored before display paths were shortened must keep their identity on rescan.
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"test-keys:AWS:/workspace/repo/new_key:{FakeSecret}")));

        Assert.Equal(expected, TruffleHogResultParser.Parse(FindingLine, "test-keys").Findings[0].Fingerprint);
    }
}

public sealed class FindingSeverityTests
{
    [Theory]
    [InlineData("AWS", true, "", "critical")]
    [InlineData("PrivateKey", false, "", "high")]
    [InlineData("Github", false, "", "medium")]
    [InlineData("RenovateDependency", false, "major", "medium")]
    [InlineData("RenovateDependency", false, "minor", "low")]
    [InlineData("RenovateConfig", false, "", "low")]
    public void MapsToSingleServerSideSeverity(string detector, bool verified, string updateType, string expected) =>
        Assert.Equal(expected, FindingSeverity.For(new FindingEntity { Detector = detector, Verified = verified, UpdateType = updateType }));
}

public sealed class ScanOutputSanitizerTests
{
    [Fact]
    public void StripsUrlCredentialsAndCapsSize()
    {
        Assert.Equal("fatal: https://***@github.com/x/y.git", ScanOutputSanitizer.Sanitize("fatal: https://user:tok@github.com/x/y.git"));
        Assert.True(ScanOutputSanitizer.Sanitize(new string('a', 200_000)).Length < ScanOutputSanitizer.MaxLength + 50);
    }
}

public sealed class ScannerIdTests
{
    [Theory]
    [InlineData("semgrep", true)]
    [InlineData("my-scanner-2", true)]
    [InlineData("Semgrep", false)]
    [InlineData("has space", false)]
    [InlineData("a/b", false)]
    [InlineData("-leading", false)]
    [InlineData("trailing-", false)]
    public void ScannerIdsMustBeValidKubernetesLabelValues(string id, bool valid) =>
        Assert.Equal(valid, ApiEndpoints.IsValidScannerId(id));
}

public sealed class ScanJobFactoryTests
{
    private static k8s.Models.V1Job Build(string scannerId, string mode = "incremental", string commandJson = "[]", string argsJson = "[]") =>
        ScanJobFactory.Build(
            new ExecutionEntity { Id = "e1", Attempt = 1, Mode = mode },
            new RepositoryEntity { Id = "repo-1", CloneUrl = "https://github.com/x/y.git", DefaultBranch = "main" },
            new ScannerEntity { Id = scannerId, Image = "img:1", CommandJson = commandJson, ArgsJson = argsJson },
            "default");

    private static IList<k8s.Models.V1EnvVar> ScanEnv(k8s.Models.V1Job job) => job.Spec.Template.Spec.Containers[0].Env;
    private static string CloneDepth(k8s.Models.V1Job job) => job.Spec.Template.Spec.InitContainers[0].Env.Single(e => e.Name == "DEPTH").Value;

    [Fact]
    public void GitHubTokenIsOnlyMountedForRenovate()
    {
        Assert.Contains(ScanEnv(Build("renovate")), e => e.Name == "GITHUB_COM_TOKEN" && e.ValueFrom.SecretKeyRef.Optional == true);
        Assert.DoesNotContain(ScanEnv(Build("custom-scanner")), e => e.Name == "GITHUB_COM_TOKEN");
    }

    [Fact]
    public void FullModeClonesCompleteHistoryIncrementalIsShallow()
    {
        Assert.Equal("--depth 50", CloneDepth(Build("trufflehog", "incremental")));
        Assert.Equal("", CloneDepth(Build("trufflehog", "full")));
    }

    [Fact]
    public void SubstitutesAllDocumentedPlaceholders()
    {
        var job = Build("custom", "full", argsJson: """["{{workspace}}","{{repository}}","{{branch}}","{{mode}}"]""");

        Assert.Equal(["/workspace/repo", "repo-1", "main", "full"], job.Spec.Template.Spec.Containers[0].Args);
    }

    [Fact]
    public void PodsAreHardenedAndBounded()
    {
        var spec = Build("trufflehog").Spec.Template.Spec;

        Assert.True(spec.SecurityContext.RunAsNonRoot);
        Assert.False(spec.AutomountServiceAccountToken);
        Assert.All(spec.Containers.Concat(spec.InitContainers), c =>
        {
            Assert.False(c.SecurityContext.AllowPrivilegeEscalation);
            Assert.True(c.SecurityContext.ReadOnlyRootFilesystem);
            Assert.Contains("ALL", c.SecurityContext.Capabilities.Drop);
            Assert.True(c.Resources.Limits.ContainsKey("memory"));
        });
    }
}

public sealed class RenovatePullRequestModeTests
{
    // Shapes captured from a real `renovate --platform=gitea` run against the compose Gitea.
    private const string UpdatesLine =
        """{"level":20,"msg":"packageFiles with updates","config":{"npm":[{"packageFile":"package.json","deps":[{"depName":"minimist","currentValue":"1.2.5","updates":[{"newValue":"1.2.8","updateType":"patch"}]}]}]}}""";

    private const string ReportLine =
        """{"level":30,"msg":"Printing report","report":{"repositories":{"aegis/renovate-demo":{"problems":[{"level":40,"msg":"No github.com token has been configured. Skipping release notes retrieval"}],"branches":[{"branchName":"renovate/minimist-1.x","prNo":1,"prTitle":"Update dependency minimist to v1.2.8","result":"pr-created","upgrades":[{"depName":"minimist","packageFile":"package.json","currentValue":"1.2.5","newValue":"1.2.8","updateType":"patch"}]}]}}}}""";

    [Fact]
    public void LinksPullRequestsToTheirFindingsAndRunsAndIgnoresTokenNoise()
    {
        var result = RenovateResultParser.Parse(UpdatesLine + "\n" + ReportLine, "demo");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("RenovateDependency", finding.Detector); // the release-notes warning is NOT a RenovateConfig finding
        Assert.Equal(1, finding.PullRequestNumber);
        var link = Assert.Single(result.Links!);
        Assert.Equal(("pull_request", 1), (link.Kind, link.Number));
        Assert.Contains("release notes", result.Output);
    }

    [Theory]
    [InlineData("""{"msg":"Created new Issue #9"}""", 9)]
    [InlineData("""{"msg":"Issue #9 is open and up to date - nothing to do"}""", 9)]
    public void FindsTheDependencyDashboardIssueNewOrExisting(string followUp, int number)
    {
        var log = """{"msg":"ensureIssue(Dependency Dashboard)"}""" + "\n" + followUp;

        var link = Assert.Single(RenovateResultParser.Parse(log, "demo").Links!);

        Assert.Equal(("issue", number), (link.Kind, link.Number));
    }

    [Fact]
    public void ForgeRunsAreMarkedPullRequestModeAndLocalRunsAreNot()
    {
        Assert.True(RenovateResultParser.Parse("""{"msg":"ensureDependencyDashboard()"}""", "r").PullRequestMode);
        Assert.False(RenovateResultParser.Parse(UpdatesLine, "r").PullRequestMode);
    }

    [Fact]
    public void IssuesOtherThanTheDashboardAreNotMistakenForIt()
    {
        var log = """{"msg":"Found Issue #3"}""" + "\n" + """{"msg":"ensureIssueClosing(Action Required: Fix Renovate Configuration)"}""";

        Assert.Empty(RenovateResultParser.Parse(log, "demo").Links!);
    }
}

public sealed class ForgeLinksTests
{
    private static readonly Dictionary<string, string> Overrides = new() { ["http://gitea:3000"] = "http://localhost:3300" };

    [Theory]
    [InlineData("https://github.com/o/r.git", "https://github.com/o/r")]
    [InlineData("git@github.com:o/r.git", "https://github.com/o/r")]
    [InlineData("ssh://git@gitlab.com/g/sub/r.git", "https://gitlab.com/g/sub/r")]
    [InlineData("https://user:tok@github.com/o/r", "https://github.com/o/r")] // credentials never leak into links
    public void NormalizesCloneUrlsToBrowserUrls(string clone, string expected) => Assert.Equal(expected, ForgeLinks.RepoUrl(clone));

    [Fact]
    public void AppliesInternalToPublicUrlOverride() =>
        Assert.Equal("http://localhost:3300/aegis/demo", ForgeLinks.RepoUrl("http://gitea:3000/aegis/demo.git", Overrides));

    [Fact]
    public void BuildsProviderSpecificFileCommitAndPullRequestUrls()
    {
        Assert.Equal("https://github.com/o/r/blob/abc/dir/a%20b.txt#L7", ForgeLinks.FileUrl("github", "https://github.com/o/r", "abc", "dir/a b.txt", 7));
        Assert.Equal("https://gitlab.com/o/r/-/blob/abc/k#L2", ForgeLinks.FileUrl("gitlab", "https://gitlab.com/o/r", "abc", "k", 2));
        Assert.Equal("http://h/o/r/src/commit/abc/k", ForgeLinks.FileUrl("gitea", "http://h/o/r", "abc", "k", null));
        Assert.Equal("https://gitlab.com/o/r/-/commit/abc", ForgeLinks.CommitUrl("gitlab", "https://gitlab.com/o/r", "abc"));
        Assert.Equal("http://h/o/r/pulls/4", ForgeLinks.PullRequestUrl("gitea", "http://h/o/r", 4));
        Assert.Equal("https://gitlab.com/o/r/-/merge_requests/4", ForgeLinks.PullRequestUrl("gitlab", "https://gitlab.com/o/r", 4));
    }

    [Fact]
    public void DerivesRenovateSlugAndOrigin()
    {
        Assert.Equal("aegis/renovate-demo", ForgeLinks.Slug("http://gitea:3000/aegis/renovate-demo.git"));
        Assert.Equal("http://gitea:3000", ForgeLinks.Origin("http://gitea:3000/aegis/renovate-demo.git"));
    }

    [Fact]
    public void ThereIsNoLinkWithoutACommit() => Assert.Null(ForgeLinks.FileUrl("github", "https://github.com/o/r", "", "a", 1));
}

public sealed class PreviewMaskingTests
{
    [Fact]
    public void NeverExposesAFullSecretOrKeyMaterial()
    {
        Assert.Equal("AKIAQY…  (20 chars)", TruffleHogResultParser.MaskPreview("AKIAQYLPMN5HHHFPZAM2"));
        Assert.Equal("-----BEGIN OPENSSH PRIVATE KEY-----", TruffleHogResultParser.MaskPreview("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAACmFl"));
        Assert.Equal("https://admin:********@host/x", TruffleHogResultParser.MaskPreview("https://admin:********@host/x"));
        Assert.Equal("", TruffleHogResultParser.MaskPreview(""));
    }

    [Fact]
    public void ParsedFindingPreviewIsMasked()
    {
        const string line = """{"SourceMetadata":{"Data":{"Filesystem":{"file":"/workspace/repo/k","line":1}}},"DetectorName":"AWS","Verified":true,"Raw":"AKIAFAKEFAKEFAKE1234","Redacted":"AKIAFAKEFAKEFAKE1234"}""";

        var finding = Assert.Single(TruffleHogResultParser.Parse(line, "r").Findings);

        Assert.DoesNotContain("FAKEFAKE1234", finding.Preview);
    }
}

public sealed class CsvExportTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    public void NeutralizesSpreadsheetFormulasFromRepositoryContent(string input, string expected) =>
        Assert.Equal(expected, ApiEndpoints.CsvField(input));
}

public sealed class SettingsValidationTests
{
    private static Dictionary<string, System.Text.Json.JsonElement> Settings(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;

    [Theory]
    [InlineData("""{"maxConcurrency":4,"retentionDays":90,"defaultScanMode":"full","blockVerifiedFindings":true,"renovateCreatePullRequests":false,"webhookUrl":null}""")]
    [InlineData("""{"webhookUrl":"","forgeUrlOverrides":{"http://gitea:3000":"http://localhost:3300"},"somethingUnknown":123}""")]
    public void AcceptsWellFormedSettings(string json) => Assert.Null(ApiEndpoints.ValidateSettings(Settings(json)));

    [Theory]
    [InlineData("""{"forgeUrlOverrides":"x"}""")]
    [InlineData("""{"forgeUrlOverrides":{"http://a":5}}""")]
    [InlineData("""{"forgeUrlOverrides":{"javascript:alert(1)":"http://ok"}}""")]
    [InlineData("""{"forgeUrlOverrides":{"http://a":"javascript:alert(1)"}}""")]
    [InlineData("""{"defaultScanMode":"sideways"}""")]
    [InlineData("""{"defaultScanMode":7}""")]
    [InlineData("""{"maxConcurrency":0}""")]
    [InlineData("""{"maxConcurrency":"4"}""")]
    [InlineData("""{"retentionDays":-1}""")]
    [InlineData("""{"renovateCreatePullRequests":"yes"}""")]
    [InlineData("""{"webhookUrl":"ftp://x"}""")]
    public void RejectsMalformedSettingsThatWouldBreakLaterReads(string json) => Assert.NotNull(ApiEndpoints.ValidateSettings(Settings(json)));

    [Theory]
    [InlineData("Ready", null, "never-scanned")]
    [InlineData("Ready", "failed", "failing")]
    [InlineData("Ready", "succeeded", "healthy")]
    [InlineData("Blocked", "succeeded", "blocked")]
    public void RepositoryHealthComesFromTheLastFinishedScan(string status, string? last, string expected) =>
        Assert.Equal(expected, ApiEndpoints.HealthOf(new RepositoryEntity { Status = status }, last));
}

public sealed class FailureReasonTests
{
    [Fact]
    public void SurfacesTheFirstErrorLineOfAFailedRun()
    {
        const string output = "[clone]\nCloning into '/workspace/repo'...\nfatal: unable to access 'https://x/y.git/': Could not resolve host: x\n\n[scan]\n";

        Assert.StartsWith("fatal: unable to access", ApiEndpoints.FailureReasonOf(output));
        Assert.Null(ApiEndpoints.FailureReasonOf(null));
        Assert.Null(ApiEndpoints.FailureReasonOf("all good"));
    }
}

public sealed class GitHistoryScanTests
{
    // Shape of `trufflehog git --json` output: SourceMetadata.Data.Git carries the introducing commit and a repo-relative path.
    private const string GitFinding =
        """{"SourceMetadata":{"Data":{"Git":{"commit":"65845d5b1118f7cf1ed03d964402508addc716bb","file":"new_key","line":2}}},"DetectorName":"AWS","Verified":true,"Raw":"AKIAFAKEFAKEFAKE1234","Redacted":"AKIAFAKEFAKEFAKE1234"}""";
    private const string FsFinding =
        """{"SourceMetadata":{"Data":{"Filesystem":{"file":"/workspace/repo/new_key","line":2}}},"DetectorName":"AWS","Verified":true,"Raw":"AKIAFAKEFAKEFAKE1234"}""";

    [Fact]
    public void RecordsTheIntroducingCommitAndRelativePath()
    {
        var finding = Assert.Single(TruffleHogResultParser.Parse(GitFinding, "r").Findings);

        Assert.Equal("65845d5b1118f7cf1ed03d964402508addc716bb", finding.IntroducedCommit);
        Assert.Equal("new_key", finding.SafePath);
        Assert.Equal("line 2", finding.Detail);
    }

    [Fact]
    public void GitAndFilesystemScansOfTheSameSecretShareAFingerprint()
    {
        // Otherwise switching an existing repository to full (history) scans would duplicate every finding.
        Assert.Equal(
            TruffleHogResultParser.Parse(FsFinding, "r").Findings[0].Fingerprint,
            TruffleHogResultParser.Parse(GitFinding, "r").Findings[0].Fingerprint);
    }
}

public sealed class JobShapeV2Tests
{
    private static k8s.Models.V1Job Build(string scanner, string mode, string provider = "gitea", string? cachePvc = null, string? tokenKey = null, bool pr = false) =>
        ScanJobFactory.Build(new ExecutionEntity { Id = "e1", Attempt = 1, Mode = mode },
            new RepositoryEntity { Id = "team/repo", Provider = provider, CloneUrl = "http://gitea:3000/aegis/demo.git", DefaultBranch = "main" },
            new ScannerEntity { Id = scanner, Image = "img", CommandJson = """["trufflehog","filesystem","{{workspace}}","--json","--no-update"]""", ArgsJson = """["--platform=local","--dry-run=lookup"]""" },
            "default", pr, tokenKey, cachePvc);

    [Fact]
    public void FullTruffleHogScansScanGitHistoryIncrementalScansScanFiles()
    {
        Assert.Equal(["trufflehog", "filesystem", "/workspace/repo", "--json", "--no-update"], Build("trufflehog", "incremental").Spec.Template.Spec.Containers[0].Command);
        Assert.Equal(["trufflehog", "git", "file:///workspace/repo", "--json", "--no-update"], Build("trufflehog", "full").Spec.Template.Spec.Containers[0].Command);
    }

    [Fact]
    public void SharedGitCacheMountsTheClaimLocksAndKeysByRepository()
    {
        var spec = Build("trufflehog", "incremental", cachePvc: "aegis-cache").Spec.Template.Spec;
        var clone = spec.InitContainers[0];

        Assert.Contains(spec.Volumes, v => v.Name == "gitcache" && v.PersistentVolumeClaim.ClaimName == "aegis-cache");
        Assert.Contains(clone.VolumeMounts, m => m.MountPath == "/cache");
        Assert.Contains("flock", clone.Command[2]);
        Assert.Contains("--mirror", clone.Command[2]);
        var key = clone.Env.Single(e => e.Name == "CACHE_KEY").Value;
        Assert.StartsWith("team_repo-", key);
        Assert.NotEqual(ScanJobFactory.CacheKey("a/b"), ScanJobFactory.CacheKey("a_b"));
        Assert.Contains("remote set-url origin", clone.Command[2]); // a changed clone URL must not leave the mirror pointing at the old repository
        Assert.DoesNotContain(Build("trufflehog", "incremental").Spec.Template.Spec.Volumes, v => v.Name == "gitcache");
    }

    [Fact]
    public void PullRequestModeUsesTheProviderSpecificTokenKey()
    {
        var env = Build("renovate", "incremental", pr: true, tokenKey: "renovate-token-gitea").Spec.Template.Spec.Containers[0].Env;

        Assert.Equal("renovate-token-gitea", env.Single(e => e.Name == "RENOVATE_TOKEN").ValueFrom.SecretKeyRef.Key);
        Assert.Equal("gitea", env.Single(e => e.Name == "RENOVATE_PLATFORM").Value);
    }
}

public sealed class WebhookAndSettingsV2Tests
{
    private static Dictionary<string, System.Text.Json.JsonElement> S(string json) => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;

    [Theory]
    [InlineData("""{"slaDays":{"critical":3,"high":14}}""", true)]
    [InlineData("""{"slaDays":{"urgent":3}}""", false)]
    [InlineData("""{"slaDays":{"critical":0}}""", false)]
    [InlineData("""{"slaDays":5}""", false)]
    [InlineData("""{"webhookSecret":"s3cret"}""", true)]
    [InlineData("""{"webhookSecret":5}""", false)]
    public void ValidatesNewSettings(string json, bool valid) => Assert.Equal(valid, ApiEndpoints.ValidateSettings(S(json)) is null);
}

public sealed class RepositoryIdRoutingTests
{
    [Theory]
    [InlineData("team~edge-service", "team/edge-service")]
    [InlineData("plain", "plain")]
    [InlineData("a~b~c", "a/b/c")]
    public void ClientSafeIdsMapBackToSlashIds(string wire, string id) => Assert.Equal(id, ApiEndpoints.DecodeId(wire));
}

public sealed class FindingLifecycleTests
{
    private static AegisDbContext NewDb()
    {
        var db = new AegisDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static FindingEntity Finding(string fp, string introduced = "", bool verified = true, string state = "open") =>
        new() { Fingerprint = fp, RepositoryId = "r", Scanner = "trufflehog", Detector = "AWS", Verified = verified, State = state, IntroducedCommit = introduced };

    private static ScanParseResult Seen(params string[] fingerprints) =>
        new(fingerprints.Select(f => Finding(f)).ToList(), Authoritative: true, "", null);

    [Fact]
    public async Task IncrementalScansDoNotResolveSecretsOnlyHistoryCanVouchFor()
    {
        await using var db = NewDb();
        db.Findings.AddRange(Finding("history", introduced: "abc123"), Finding("files-only"));
        await db.SaveChangesAsync();

        await ScanExecutionWorker.UpsertFindingsAsync(db, new ExecutionEntity { Id = "e", RepositoryId = "r", Scanner = "trufflehog", Mode = "incremental" }, Seen(), null, default);
        await db.SaveChangesAsync();

        Assert.Equal("open", db.Findings.Find("history")!.State);    // deleted at HEAD, still in history: NOT fixed
        Assert.Equal("open", db.Findings.Find("files-only")!.State); // neither is a secret an incremental scan merely stopped seeing

        await ScanExecutionWorker.UpsertFindingsAsync(db, new ExecutionEntity { Id = "e2", RepositoryId = "r", Scanner = "trufflehog", Mode = "full" }, Seen(), null, default);
        await db.SaveChangesAsync();
        Assert.Equal("resolved", db.Findings.Find("history")!.State);   // a full scan that no longer finds it in history may resolve it
        Assert.Equal("resolved", db.Findings.Find("files-only")!.State);
    }

    [Fact]
    public async Task IncrementalScansStillResolveDependencyFindings()
    {
        await using var db = NewDb();
        db.Findings.Add(new FindingEntity { Fingerprint = "dep", RepositoryId = "r", Scanner = "renovate", Detector = "RenovateDependency", State = "open" });
        await db.SaveChangesAsync();

        await ScanExecutionWorker.UpsertFindingsAsync(db, new ExecutionEntity { Id = "e", RepositoryId = "r", Scanner = "renovate", Mode = "incremental" }, new ScanParseResult([], true, ""), null, default);
        await db.SaveChangesAsync();

        Assert.Equal("resolved", db.Findings.Find("dep")!.State);
    }

    [Fact]
    public async Task OnlyOpenVerifiedFindingsCountTowardsBlockingARepository()
    {
        await using var db = NewDb();
        db.Findings.Add(Finding("accepted", state: "ignored"));
        await db.SaveChangesAsync();

        var blockedByIgnored = await ScanExecutionWorker.UpsertFindingsAsync(db, new ExecutionEntity { Id = "e", RepositoryId = "r", Scanner = "trufflehog", Mode = "incremental" }, Seen("accepted"), null, default);
        var blockedByNew = await ScanExecutionWorker.UpsertFindingsAsync(db, new ExecutionEntity { Id = "e", RepositoryId = "r", Scanner = "trufflehog", Mode = "incremental" }, Seen("accepted", "fresh"), null, default);

        Assert.False(blockedByIgnored); // an ignored / false-positive decision sticks
        Assert.True(blockedByNew);
    }
}

public sealed class EnrichmentTests
{
    [Fact]
    public void FindsTheManifestLineOfADependency()
    {
        const string manifest = "{\n  \"name\": \"demo\",\n  \"dependencies\": {\n    \"express\": \"4.17.1\",\n    \"lodash\": \"4.17.20\"\n  }\n}";

        Assert.Equal(4, FindingEnricher.FindLine(manifest, "express"));
        Assert.Equal(5, FindingEnricher.FindLine(manifest, "lodash"));
        Assert.Equal(0, FindingEnricher.FindLine(manifest, "left-pad"));
        Assert.Equal(2, FindingEnricher.FindLine("a\nrequests==2.0\n", "requests")); // non-JSON manifests: first bare mention
    }

    [Fact]
    public void ReducesAnOsvResponseToLinkedAdvisories()
    {
        const string osv = """{"vulns":[{"id":"GHSA-xvch-5gv4-984h","summary":"Prototype Pollution in minimist","modified":"x"},{"id":"CVE-2021-44906"}]}""";

        var json = FindingEnricher.AdvisoriesJson(osv);

        Assert.Contains("GHSA-xvch-5gv4-984h", json);
        Assert.Contains("https://osv.dev/vulnerability/GHSA-xvch-5gv4-984h", json);
        Assert.Equal("", FindingEnricher.AdvisoriesJson("""{}"""));
        Assert.Equal("", FindingEnricher.AdvisoriesJson("not json"));
    }

    [Fact]
    public void AKnownVulnerabilityMakesADependencyFindingHighSeverity()
    {
        var plain = new FindingEntity { Detector = "RenovateDependency", UpdateType = "patch" };
        var vulnerable = new FindingEntity { Detector = "RenovateDependency", UpdateType = "patch", Advisories = """[{"id":"GHSA-1"}]""" };

        Assert.Equal("low", FindingSeverity.For(plain));
        Assert.Equal("high", FindingSeverity.For(vulnerable));
    }

    [Fact]
    public void ParserCapturesWhatTheEnricherNeeds()
    {
        const string line = """{"level":20,"msg":"packageFiles with updates","config":{"npm":[{"packageFile":"package.json","deps":[{"depName":"minimist","datasource":"npm","currentValue":"1.2.5","currentVersion":"1.2.5","updates":[{"newValue":"1.2.8","updateType":"patch"}]}]}]}}""";

        var dependency = Assert.Single(RenovateResultParser.Parse(line, "r").Dependencies!);

        Assert.Equal(("minimist", "npm", "1.2.5", "package.json"), (dependency.Name, dependency.Datasource, dependency.Version, dependency.PackageFile));
    }

    [Fact]
    public void BuildsRawFileUrlsPerForge()
    {
        Assert.Equal("http://gitea:3000/aegis/demo/raw/commit/abc/package.json", ForgeLinks.RawUrl("gitea", "http://gitea:3000/aegis/demo", "abc", "package.json"));
        Assert.Equal("https://raw.githubusercontent.com/o/r/abc/package.json", ForgeLinks.RawUrl("github", "https://github.com/o/r", "abc", "package.json"));
        Assert.Equal("https://gitlab.com/g/r/-/raw/abc/package.json", ForgeLinks.RawUrl("gitlab", "https://gitlab.com/g/r", "abc", "package.json"));
    }
}

public sealed class ManifestFetchSafetyTests
{
    private static readonly RepositoryEntity Gitea = new() { Provider = "gitea", CloneUrl = "http://gitea:3000/aegis/demo.git" };
    private static readonly RepositoryEntity GitHub = new() { Provider = "github", CloneUrl = "https://github.com/o/r.git" };

    [Theory]
    [InlineData("../../admin/secret")]
    [InlineData("a/../b/package.json")]
    [InlineData("./package.json")]
    [InlineData("a\\b")] // a real backslash
    public void RejectsTraversalAndBackslashPaths(string file)
    {
        Assert.Null(FindingEnricher.ManifestUri(Gitea, "abc", file));
        Assert.Null(FindingEnricher.ManifestUri(GitHub, "abc", file));
    }

    [Fact]
    public void OnlyBuildsUrlsOnTheRepositoryOwnHost()
    {
        Assert.Equal("http://gitea:3000/aegis/demo/raw/commit/abc/package.json", FindingEnricher.ManifestUri(Gitea, "abc", "package.json")!.ToString());
        Assert.Equal("raw.githubusercontent.com", FindingEnricher.ManifestUri(GitHub, "abc", "sub/package.json")!.Host);
        Assert.Null(FindingEnricher.ManifestUri(Gitea, "", "package.json")); // no commit, no fetch
    }
}
