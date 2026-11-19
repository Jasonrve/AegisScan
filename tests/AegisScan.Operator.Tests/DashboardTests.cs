using System.Text.Json;
using AegisScan.KubeOps.Api;
using AegisScan.KubeOps.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class DashboardTests
{
    [Fact]
    public async Task RanksRisksAndSeparatesNewCvesFromTheBaselineScan()
    {
        await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        db.Database.EnsureCreated();
        var baseline = DateTimeOffset.UtcNow.AddHours(-3);
        db.Repositories.AddRange(new RepositoryEntity { Id = "noisy", Name = "noisy" }, new RepositoryEntity { Id = "secret", Name = "secret" }, new RepositoryEntity { Id = "quiet", Name = "quiet" });
        FindingEntity Cve(string id, string repo, string severity, DateTimeOffset seen) => new() { Fingerprint = Guid.NewGuid().ToString("N"), RepositoryId = repo, Scanner = "sbom", Detector = id, Severity = severity, Detail = "openssl 1.0", State = "open", FirstSeenAt = seen };
        db.Findings.AddRange(
            // Baseline scan of two repositories (one batch); a later rescan of "noisy" brings CVE-NEW.
            Cve("CVE-1", "noisy", "critical", baseline), Cve("CVE-1", "quiet", "critical", baseline), Cve("CVE-2", "noisy", "high", baseline), Cve("CVE-2", "noisy", "high", baseline.AddMilliseconds(5)),
            Cve("CVE-NEW", "noisy", "high", DateTimeOffset.UtcNow.AddMinutes(-10)),
            // A built-in finding has no stored severity: a verified secret is derived as critical and outranks the CVE-heavy repository.
            new FindingEntity { Fingerprint = "s1", RepositoryId = "secret", Scanner = "trufflehog", Detector = "AWS", Verified = true, State = "open", FirstSeenAt = baseline });
        await db.SaveChangesAsync();

        var result = JsonSerializer.SerializeToElement(await DashboardEndpoints.BuildAsync(db, 7));
        var totals = result.GetProperty("totals");
        Assert.Equal(3, totals.GetProperty("critical").GetInt32()); // 2 CVE-1 + the derived verified secret
        Assert.Equal(3, totals.GetProperty("high").GetInt32());
        Assert.Equal(1, totals.GetProperty("verifiedSecrets").GetInt32());
        Assert.Equal("secret,noisy,quiet", string.Join(',', result.GetProperty("repos").EnumerateArray().Select(r => r.GetProperty("name").GetString())));

        var fresh = Assert.Single(result.GetProperty("newCves").EnumerateArray()); // baseline CVEs are not "new"
        Assert.Equal("CVE-NEW", fresh.GetProperty("Id").GetString());
        var widest = result.GetProperty("widespread").EnumerateArray().First();
        Assert.Equal("CVE-1", widest.GetProperty("Id").GetString());
        Assert.Equal(2, widest.GetProperty("Repos").GetInt32()); // distinct repositories, not occurrences
    }
}
