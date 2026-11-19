using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Api;

/// <summary>"What is running where": the latest run per repository and scanner, open finding counts, and the runs in flight. Feeds the Activity matrix and the repository scan-flow graph.</summary>
public static class ActivityEndpoints
{
    public static void MapActivityApi(this RouteGroupBuilder api) =>
        api.MapGet("/activity", async (AegisDbContext db, string? repositoryId) => Results.Ok(await BuildAsync(db, repositoryId)))
            .RequireAuthorization(AegisRoles.Viewer);

    public sealed record RunCell(string Id, string RepositoryId, string Scanner, string Mode, string Status, int Attempt, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, DateTimeOffset? NotBefore, int FindingCount, bool OutputInterpreted, string? JobName, string CommitSHA);

    public static async Task<object> BuildAsync(AegisDbContext db, string? repositoryId)
    {
        var runs = db.Executions.AsNoTracking().AsQueryable();
        var findings = db.Findings.AsNoTracking().Where(f => f.State == "open");
        if (!string.IsNullOrWhiteSpace(repositoryId))
        {
            runs = runs.Where(e => e.RepositoryId == repositoryId);
            findings = findings.Where(f => f.RepositoryId == repositoryId);
        }
        // Projected so the multi-MB logs are never loaded.
        var latest = await runs.GroupBy(e => new { e.RepositoryId, e.Scanner })
            .Select(g => g.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
                .Select(e => new RunCell(e.Id, e.RepositoryId, e.Scanner, e.Mode, e.Status, e.Attempt, e.CreatedAt, e.StartedAt, e.FinishedAt, e.NotBefore, e.FindingCount, e.OutputInterpreted, e.JobName, e.CommitSHA)).First())
            .ToListAsync();
        var active = await runs.Where(e => e.Status == "queued" || e.Status == "preparing" || e.Status == "running").OrderBy(e => e.CreatedAt)
            .Select(e => new RunCell(e.Id, e.RepositoryId, e.Scanner, e.Mode, e.Status, e.Attempt, e.CreatedAt, e.StartedAt, e.FinishedAt, e.NotBefore, e.FindingCount, e.OutputInterpreted, e.JobName, e.CommitSHA))
            .Take(200).ToListAsync();
        var open = await findings.GroupBy(f => new { f.RepositoryId, f.Scanner }).Select(g => new { repositoryId = g.Key.RepositoryId, scanner = g.Key.Scanner, count = g.Count() }).ToListAsync();
        var scanners = await db.Scanners.AsNoTracking().Where(s => s.Enabled).OrderByDescending(s => s.BuiltIn).ThenBy(s => s.Name).Select(s => new { s.Id, s.Name, s.Category }).ToListAsync();
        // Severity is derived in C# (FindingSeverity), so the split is only computed for the one-repository call, where the set is small.
        var severities = string.IsNullOrWhiteSpace(repositoryId) ? [] : (await findings.Take(5000).ToListAsync())
            .GroupBy(f => (f.Scanner, Severity: FindingSeverity.For(f))).Select(g => new { scanner = g.Key.Scanner, severity = g.Key.Severity, count = g.Count() }).ToList();
        return new { scanners, latest, active, open, severities, generatedAt = DateTimeOffset.UtcNow };
    }
}
