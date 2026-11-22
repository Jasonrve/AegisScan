using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Api;

/// <summary>Dashboard aggregates computed server-side over every open finding (the UI only ever loads a capped page of them): riskiest repositories, new CVEs and the most widespread ones.</summary>
public static class DashboardEndpoints
{
    private static readonly string[] Severities = ["critical", "high", "medium", "low"];
    /// <summary>Findings of one upsert share a timestamp, so anything first seen within this of a repository+scanner's first findings belongs to its baseline scan, not to "new".</summary>
    private static readonly TimeSpan BaselineMargin = TimeSpan.FromMinutes(5);

    public static void MapDashboardApi(this RouteGroupBuilder api) =>
        api.MapGet("/dashboard", async (AegisDbContext db, string[]? repositoryId, int days = 7) => Results.Ok(await BuildAsync(db, Math.Clamp(days, 1, 365), repositoryId)))
            .RequireAuthorization(AegisRoles.Viewer);

    private static bool IsCve(string detector) => detector.StartsWith("CVE-", StringComparison.Ordinal) || detector.StartsWith("GHSA-", StringComparison.Ordinal);
    private static int Rank(string severity) => Array.IndexOf(Severities, severity) is var i and >= 0 ? Severities.Length - i : 0;

    public sealed record CveRow(string Id, string Severity, int Repos, int Occurrences, string Package, bool FixAvailable, string[] RepoIds, DateTimeOffset FirstSeen);
    internal sealed record CveItem(string Detector, string RepositoryId, string Scanner, string Severity, string Detail, DateTimeOffset FirstSeenAt);

    private static CveRow ToRow(IGrouping<string, CveItem> g)
    {
        var first = g.First().Detail;
        var cut = first.IndexOf(" — ", StringComparison.Ordinal); // SBOM detail: "package version — Upgrade package to version X"
        var repoIds = g.Select(f => f.RepositoryId).Distinct().ToArray();
        return new CveRow(g.Key, g.Select(f => f.Severity).OrderByDescending(Rank).First(), repoIds.Length, g.Count(), cut >= 0 ? first[..cut] : first, g.Any(f => f.Detail.Contains(" — Upgrade", StringComparison.Ordinal)), repoIds.Take(8).ToArray(), g.Min(f => f.FirstSeenAt));
    }

    /// <summary>Open vulnerability findings carry the advisory id as their detector and "package version" as their detail. Fresh = first seen after the repository+scanner's baseline scan, within <paramref name="since"/>.</summary>
    internal static async Task<(List<CveItem> Open, List<CveItem> Fresh)> LoadCvesAsync(AegisDbContext db, HashSet<string> managed, DateTimeOffset since)
    {
        var open = (await db.Findings.AsNoTracking().Where(f => f.State == "open" && (f.Detector.StartsWith("CVE-") || f.Detector.StartsWith("GHSA-")))
            .Select(f => new CveItem(f.Detector, f.RepositoryId, f.Scanner, f.Severity, f.Detail, f.FirstSeenAt)).Take(100000).ToListAsync()).Where(f => managed.Contains(f.RepositoryId)).ToList();
        var baseline = (await db.Findings.AsNoTracking().GroupBy(f => new { f.RepositoryId, f.Scanner }).Select(g => new { g.Key.RepositoryId, g.Key.Scanner, First = g.Min(f => f.FirstSeenAt) }).ToListAsync())
            .ToDictionary(b => (b.RepositoryId, b.Scanner), b => b.First);
        return (open, open.Where(f => f.FirstSeenAt >= since && f.FirstSeenAt > baseline[(f.RepositoryId, f.Scanner)] + BaselineMargin).ToList());
    }

    public static async Task<object> BuildAsync(AegisDbContext db, int days, IReadOnlyCollection<string>? scope = null)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var repos = await db.Repositories.AsNoTracking().Where(r => scope == null || scope.Count == 0 || scope.Contains(r.Id)).Select(r => new { r.Id, r.Name, r.Status }).ToListAsync();
        var managed = repos.Select(r => r.Id).ToHashSet();
        var categories = await db.Scanners.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Category);

        var (counts, verified) = await RiskCounts.LoadAsync(db, managed);

        var perRepo = counts.GroupBy(c => c.RepositoryId).ToDictionary(g => g.Key, g => g.ToList());
        var riskiest = repos.Select(r =>
        {
            var rows = perRepo.GetValueOrDefault(r.Id) ?? [];
            int Of(string s) => rows.Where(c => c.Severity == s).Sum(c => c.Count);
            return new
            {
                id = r.Id, name = r.Name, blocked = r.Status == "Blocked", verified = verified.GetValueOrDefault(r.Id),
                critical = Of("critical"), high = Of("high"), medium = Of("medium"), low = Of("low"),
                categories = rows.GroupBy(c => categories.GetValueOrDefault(c.Scanner, "other")).ToDictionary(g => g.Key, g => g.Sum(c => c.Count))
            };
        }).Where(r => r.critical + r.high + r.medium + r.low > 0 || r.blocked)
          // A live verified secret or a blocked repository outranks any number of base-image CVEs.
          .OrderByDescending(r => r.blocked || r.verified > 0).ThenByDescending(r => r.critical).ThenByDescending(r => r.high).ThenByDescending(r => r.medium).ThenBy(r => r.name, StringComparer.Ordinal)
          .Take(10).ToList();

        var (cveRows, freshRows) = await LoadCvesAsync(db, managed, since);
        var fresh = freshRows.GroupBy(f => f.Detector).ToList();
        var newCves = fresh.Select(ToRow)
            .OrderByDescending(c => Rank(c.Severity)).ThenByDescending(c => c.Repos).ThenBy(c => c.Id, StringComparer.Ordinal).Take(10).ToList();
        var widespread = cveRows.Where(f => f.Severity is "critical" or "high").GroupBy(f => f.Detector)
            .Select(ToRow)
            .OrderByDescending(c => c.Repos).ThenByDescending(c => Rank(c.Severity)).ThenBy(c => c.Id, StringComparer.Ordinal).Take(10).ToList();

        var totals = new
        {
            critical = counts.Where(c => c.Severity == "critical").Sum(c => c.Count), high = counts.Where(c => c.Severity == "high").Sum(c => c.Count),
            medium = counts.Where(c => c.Severity == "medium").Sum(c => c.Count), low = counts.Where(c => c.Severity == "low").Sum(c => c.Count),
            verifiedSecrets = verified.Values.Sum(), reposWithCritical = counts.Where(c => c.Severity == "critical").Select(c => c.RepositoryId).Distinct().Count(),
            cvesTotal = cveRows.Select(f => f.Detector).Distinct().Count(), newCves = fresh.Count
        };
        return new { days, totals, repos = riskiest, newCves, widespread, generatedAt = DateTimeOffset.UtcNow };
    }
}
