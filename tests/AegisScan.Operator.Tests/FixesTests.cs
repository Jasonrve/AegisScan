using System.Text.Json;
using AegisScan.KubeOps.Api;
using AegisScan.KubeOps.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class FixesTests
{
    private static FindingEntity F(string repo, string scanner, string detector, string path, string detail, string severity = "", bool verified = false) =>
        new() { Fingerprint = Guid.NewGuid().ToString("N"), RepositoryId = repo, Scanner = scanner, Detector = detector, SafePath = path, Detail = detail, Severity = severity, Verified = verified, State = "open" };

    [Fact]
    public async Task GroupsFindingsIntoRankedActionsAndHonoursTheRepositoryScope()
    {
        await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        db.Database.EnsureCreated();
        db.Repositories.AddRange(new RepositoryEntity { Id = "a", Name = "app" }, new RepositoryEntity { Id = "b", Name = "other" });
        db.Findings.AddRange(
            // 3 CVEs in one base image (2 with a fix) collapse to one "update the image" action; the same package in a lock file is its own action.
            F("a", "sbom", "CVE-1", "image:node:12-alpine", "libssl 1.1 — Upgrade libssl to version 1.2", "critical"), F("a", "sbom", "CVE-2", "image:node:12-alpine", "musl 1.0 — Upgrade musl to version 1.1", "high"), F("a", "sbom", "CVE-3", "image:node:12-alpine", "zlib 1.2", "high"),
            F("a", "sbom", "CVE-4", "package-lock.json", "minimatch 3.1.2 — Upgrade minimatch to version 3.1.3", "high"), F("a", "sbom", "CVE-5", "package-lock.json", "minimatch 3.1.2 — Upgrade minimatch to version 3.1.3", "medium"),
            // A live secret outranks every CVE, whatever its severity count.
            F("a", "trufflehog", "AWS", "config/.env", "line 7", verified: true),
            F("b", "sbom", "CVE-9", "image:other:1", "x 1", "critical"));
        await db.SaveChangesAsync();

        var all = JsonSerializer.SerializeToElement(await FixesEndpoints.BuildAsync(db, ["a"]));
        var fixes = all.GetProperty("fixes").EnumerateArray().ToList();
        Assert.Equal("secret,image,package", string.Join(',', fixes.Select(f => f.GetProperty("Kind").GetString())));
        Assert.Equal("config/.env:7", fixes[0].GetProperty("Where").GetString());
        var image = fixes[1];
        Assert.Equal("node:12-alpine", image.GetProperty("Title").GetString());
        Assert.Equal(3, image.GetProperty("Items").GetInt32());
        Assert.Equal(2, image.GetProperty("Fixable").GetInt32());
        Assert.Equal("critical", image.GetProperty("Severity").GetString());
        var package = fixes[2];
        Assert.Equal("minimatch 3.1.2", package.GetProperty("Title").GetString());
        Assert.Equal(2, package.GetProperty("Items").GetInt32()); // both CVEs fixed by one upgrade
        Assert.Equal("Upgrade minimatch to version 3.1.3", package.GetProperty("Action").GetString());
        Assert.DoesNotContain(fixes, f => f.GetProperty("RepositoryId").GetString() == "b");

        var everything = JsonSerializer.SerializeToElement(await FixesEndpoints.BuildAsync(db, null));
        Assert.Equal(4, everything.GetProperty("total").GetInt32());
    }
}
