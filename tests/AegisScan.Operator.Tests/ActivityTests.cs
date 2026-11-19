using System.Text.Json;
using AegisScan.KubeOps.Api;
using AegisScan.KubeOps.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class ActivityTests
{
    [Fact]
    public async Task ReportsOnlyTheLatestRunPerRepositoryAndScannerAndTheRunsInFlight()
    {
        await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        db.Database.EnsureCreated();
        var now = DateTimeOffset.UtcNow;
        ExecutionEntity Run(string id, string repo, string scanner, string status, int minutesAgo) => new() { Id = id, RepositoryId = repo, Scanner = scanner, Status = status, CreatedAt = now.AddMinutes(-minutesAgo), FindingCount = 3, OutputLog = new string('x', 1000) };
        db.Executions.AddRange(Run("old", "a", "trufflehog", "failed", 30), Run("new", "a", "trufflehog", "running", 1), Run("other", "a", "renovate", "succeeded", 5), Run("b1", "b", "trufflehog", "queued", 2));
        db.Findings.AddRange(
            new FindingEntity { Fingerprint = "f1", RepositoryId = "a", Scanner = "trufflehog", State = "open" },
            new FindingEntity { Fingerprint = "f2", RepositoryId = "a", Scanner = "trufflehog", State = "open", Verified = true },
            new FindingEntity { Fingerprint = "f3", RepositoryId = "a", Scanner = "trufflehog", State = "resolved" });
        await db.SaveChangesAsync();

        var all = JsonSerializer.SerializeToElement(await ActivityEndpoints.BuildAsync(db, null));
        Assert.Equal("b1,new,other", string.Join(',', all.GetProperty("latest").EnumerateArray().Select(c => c.GetProperty("Id").GetString()!).Order()));
        Assert.Equal("b1,new", string.Join(',', all.GetProperty("active").EnumerateArray().Select(c => c.GetProperty("Id").GetString()!))); // oldest first
        var open = Assert.Single(all.GetProperty("open").EnumerateArray());
        Assert.Equal(2, open.GetProperty("count").GetInt32()); // resolved findings are not counted

        Assert.Empty(all.GetProperty("severities").EnumerateArray()); // fleet call: no split

        var onlyA = JsonSerializer.SerializeToElement(await ActivityEndpoints.BuildAsync(db, "a"));
        Assert.Equal("critical:1,medium:1", string.Join(',', onlyA.GetProperty("severities").EnumerateArray().Select(s => $"{s.GetProperty("severity").GetString()}:{s.GetProperty("count").GetInt32()}").Order()));

        var onlyB = JsonSerializer.SerializeToElement(await ActivityEndpoints.BuildAsync(db, "b"));
        Assert.Equal("b1", Assert.Single(onlyB.GetProperty("latest").EnumerateArray()).GetProperty("Id").GetString());
    }
}
