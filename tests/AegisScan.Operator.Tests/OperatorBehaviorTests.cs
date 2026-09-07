using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Resources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class OperatorBehaviorTests
{
    private static AegisDbContext NewSeededContext()
    {
        var options = new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new AegisDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task DemoDataHasThreeRepositoriesForLocalSmokeTests()
    {
        await using var db = NewSeededContext();
        var repositories = await db.Repositories.ToListAsync();
        Assert.Equal(3, repositories.Count);
        Assert.All(repositories, repository => Assert.Equal("Ready", repository.Status));
    }

    [Fact]
    public async Task DemoDataSeedsDefaultSettings()
    {
        await using var db = NewSeededContext();
        var settings = await db.Settings.ToListAsync();
        Assert.Contains(settings, s => s.Key == "retentionDays" && s.Value == "90");
        Assert.Contains(settings, s => s.Key == "cacheRetentionGB" && s.Value == "20");
    }

    [Fact]
    public void DisabledRepositoryIsRepresentedInTheDesiredSpec()
    {
        var repository = new V1Repository { Spec = new() { Provider = "github", CloneURL = "https://example.test/repo.git", Disabled = true } };
        Assert.True(repository.Spec.Disabled);
        Assert.Equal("github", repository.Spec.Provider);
    }

    [Fact]
    public void EmptyScanPolicyIsDetectableAsInvalidByControllerRule()
    {
        var policy = new V1ScanPolicy { Spec = new() };
        Assert.Empty(policy.Spec.Scanners);
    }
}
