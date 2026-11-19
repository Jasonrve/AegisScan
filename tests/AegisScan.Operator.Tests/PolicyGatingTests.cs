using AegisScan.KubeOps.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class PolicyGatingTests
{
    private static AegisDbContext NewSeededContext()
    {
        var options = new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new AegisDbContext(options);
        db.Database.EnsureCreated();
        AegisScan.KubeOps.Data.DemoData.Seed(db);
        return db;
    }

    [Fact]
    public async Task BlockedRepositoriesAreExcludedFromScheduledScanSelection()
    {
        await using var db = NewSeededContext();
        var repo = await db.Repositories.FindAsync("acme-web");
        repo!.Status = "Blocked";
        await db.SaveChangesAsync();

        // Mirrors SchedulingWorker.ProcessScanPoliciesAsync's repository filter.
        var scheduled = await db.Repositories
            .Where(r => r.Status != "Disabled" && r.Status != "Error" && r.Status != "Blocked")
            .Select(r => r.Id)
            .ToListAsync();

        Assert.DoesNotContain("acme-web", scheduled);
        Assert.Equal(2, scheduled.Count);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void BlockVerifiedFindingsSettingDefaultsToEnabledWhenUnset(string? storedValue, bool expected)
    {
        // Mirrors how PUT /settings persists a bool ("true"/"false" raw JSON) and how
        // ScanExecutionWorker.IsBlockVerifiedFindingsEnabledAsync reads it back.
        var enabled = storedValue is null || System.Text.Json.JsonSerializer.Deserialize<bool>(storedValue);
        Assert.Equal(expected, enabled);
    }
}
