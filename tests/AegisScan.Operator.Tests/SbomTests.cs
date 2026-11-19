using System.Text.Json;
using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class SbomTests
{
    private static readonly ScannerEntity Sbom = new() { Id = "sbom", Category = "vulnerabilities", OutputFormat = "native" };
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // Real output of SbomScan.Script (Trivy 0.58.1): the repository (npm lock file), a manual alpine image, a manual image that cannot be scanned,
    // and busybox detected from a Dockerfile (stage alias and ${ARG} skipped) and a Kubernetes manifest (listed once).
    private static readonly string ScriptOutput = Fixture("sbom-script-output.txt");

    [Fact]
    public void ParsesRepositoryAndImageSbomsFromScriptOutput()
    {
        var result = SbomResultParser.Parse(ScriptOutput, "team/repo", Sbom);

        Assert.Null(result.Error);
        Assert.NotNull(result.Sboms);
        Assert.Equal(4, result.Sboms.Count);
        var repo = Assert.Single(result.Sboms, s => s.Kind == "repository");
        Assert.True(repo.ComponentCount > 0);
        var alpine = Assert.Single(result.Sboms, s => s.Ref == "alpine:3.10");
        Assert.Equal("manual", alpine.Origin);
        Assert.Equal("generated", alpine.Source);
        Assert.Equal(1, alpine.Critical); // CVE-2021-36159 is rated critical by NVD, high by Red Hat: the highest rating wins
        Assert.Equal("detected", Assert.Single(result.Sboms, s => s.Ref == "busybox:1.28").Origin);
        var failed = Assert.Single(result.Sboms, s => s.Ref == "nope.invalid/x:1");
        Assert.Equal("", failed.Document);
        Assert.Contains("FATAL", failed.Error, StringComparison.Ordinal);
        Assert.False(result.Authoritative); // one artifact failed: nothing may be auto-resolved
    }

    [Fact]
    public void TurnsVulnerableComponentsIntoFindingsKeyedByConfiguredImageRef()
    {
        var result = SbomResultParser.Parse(ScriptOutput, "team/repo", Sbom);

        var image = Assert.Single(result.Findings, f => f.Detector == "CVE-2021-36159");
        Assert.Equal("image:alpine:3.10", image.SafePath);
        Assert.Equal("critical", image.Severity);
        Assert.Equal("vulnerabilities", image.Category);
        Assert.Equal("sbom", image.Scanner);
        Assert.Contains("apk-tools 2.10.6-r0", image.Detail, StringComparison.Ordinal);
        Assert.Contains(result.Findings, f => f.SafePath == "package-lock.json"); // repository findings point at the lock file
        Assert.Equal(result.Findings.Count, result.Findings.Select(f => f.Fingerprint).Distinct().Count());
        Assert.Equal(result.Findings.Select(f => f.Fingerprint).Order(), SbomResultParser.Parse(ScriptOutput, "team/repo", Sbom).Findings.Select(f => f.Fingerprint).Order());
    }

    [Fact]
    public void ATruncatedDocumentFailsOnlyThatArtifact()
    {
        var lines = ScriptOutput.Split('\n').ToList();
        var firstEnd = lines.IndexOf("AEGIS-SBOM-END");
        lines.RemoveAt(firstEnd); // the repository block loses its end marker and swallows the next BEGIN

        var result = SbomResultParser.Parse(string.Join('\n', lines), "team/repo", Sbom);

        Assert.Null(result.Error);
        Assert.Contains("truncated", Assert.Single(result.Sboms!, s => s.Kind == "repository").Error, StringComparison.Ordinal);
        Assert.Contains(result.Sboms!, s => s.Ref == "alpine:3.10" && s.Document.Length > 0);
    }

    [Fact]
    public void RunFailsWhenNoSbomWasProduced()
    {
        Assert.NotNull(SbomResultParser.Parse("trivy: boom\n", "r", Sbom).Error);
        Assert.Contains("no SBOM could be produced", SbomResultParser.Parse("AEGIS-SBOM-FAILED repository - repository db download failed\n", "r", Sbom).Error, StringComparison.Ordinal);
        Assert.Contains("could not be decoded", SbomResultParser.Parse("AEGIS-SBOM-BEGIN repository - generated repository\n!!notbase64\nAEGIS-SBOM-END\n", "r", Sbom).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupsComponentsByLockFileAndOsAndAttachesVulnerabilities()
    {
        using var image = JsonDocument.Parse(Fixture("trivy-image.cdx.json"));
        var os = CycloneDx.Read(image.RootElement);
        Assert.All(os.Components, c => Assert.Equal("alpine 3.10.9 (OS packages)", c.Group)); // every package is reachable from the OS component
        Assert.Contains(os.Vulnerabilities.Values.SelectMany(v => v), v => v.Id == "CVE-2021-36159" && v.Severity == "critical");

        using var fs = JsonDocument.Parse(Fixture("trivy-fs.cdx.json"));
        var npm = CycloneDx.Read(fs.RootElement);
        Assert.Contains(npm.Components, c => c.Group == "package-lock.json" && c.Ecosystem == "npm" && c.Name == "react");
        Assert.Contains(npm.Components, c => c.Name == "@types/node"); // scoped packages keep their group
        Assert.DoesNotContain(npm.Components, c => c.Type == "application");
    }

    [Theory]
    [InlineData("alpine:3.10", true)]
    [InlineData("123456789012.dkr.ecr.eu-west-1.amazonaws.com/team/app:1.2.3", true)]
    [InlineData("ghcr.io/o/app@sha256:0123abcd", true)]
    [InlineData("--rm", false)]
    [InlineData("a b", false)]
    [InlineData("app;rm -rf /", false)]
    [InlineData("$(id)", false)]
    [InlineData("", false)]
    public void ImageReferencesAreRestrictedToPlainReferenceCharacters(string value, bool valid) => Assert.Equal(valid, SbomScan.IsValidImageRef(value));

    private static k8s.Models.V1Job Build(string scanner, params string[] images) =>
        ScanJobFactory.Build(new ExecutionEntity { Id = "e1", Attempt = 1, Mode = "incremental" },
            new RepositoryEntity { Id = "team/repo", Provider = "gitea", CloneUrl = "http://gitea:3000/aegis/demo.git", DefaultBranch = "main" },
            new ScannerEntity { Id = scanner, Image = "img", CommandJson = """["tool"]""", ArgsJson = "[]" }, "default", images: images);

    [Fact]
    public void SbomJobGetsScriptImageListAndOptionalRegistryCredentials()
    {
        var container = Build("sbom", "alpine:3.10", "$(touch /tmp/pwned)", "--rm").Spec.Template.Spec.Containers[0];

        Assert.Equal(["sh", "-c", SbomScan.Script], container.Command);
        Assert.Equal("alpine:3.10", container.Env.Single(e => e.Name == "AEGIS_IMAGES").Value); // invalid refs never reach the shell
        var aws = container.Env.Single(e => e.Name == "AWS_SECRET_ACCESS_KEY").ValueFrom.SecretKeyRef;
        Assert.True(aws.Optional);
        Assert.Equal("aws-secret-access-key", aws.Key);
    }

    [Fact]
    public void RegistryCredentialsAreNeverGivenToOtherScanners()
    {
        var container = Build("custom", "alpine:3.10").Spec.Template.Spec.Containers[0];

        Assert.DoesNotContain(container.Env, e => e.Name is "AEGIS_IMAGES" or "AWS_ACCESS_KEY_ID" or "TRIVY_PASSWORD");
        Assert.Equal(["tool"], container.Command);
    }
}
