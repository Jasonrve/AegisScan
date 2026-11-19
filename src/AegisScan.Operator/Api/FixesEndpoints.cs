using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Api;

public sealed record CountRow(string RepositoryId, string Scanner, string Severity, int Count);

/// <summary>Open-finding counts per repository, scanner and severity without loading entities: stored severities are grouped in SQL, the few built-in findings derive theirs in C#.</summary>
public static class RiskCounts
{
    public static async Task<(List<CountRow> Counts, Dictionary<string, int> Verified)> LoadAsync(AegisDbContext db, ISet<string> managed)
    {
        var stored = await db.Findings.AsNoTracking().Where(f => f.State == "open" && f.Severity != "")
            .GroupBy(f => new { f.RepositoryId, f.Scanner, f.Severity }).Select(g => new CountRow(g.Key.RepositoryId, g.Key.Scanner, g.Key.Severity, g.Count())).ToListAsync();
        var derived = (await db.Findings.AsNoTracking().Where(f => f.State == "open" && f.Severity == "")
            .Select(f => new { f.RepositoryId, f.Scanner, f.Detector, f.Verified, f.UpdateType, f.Advisories }).ToListAsync())
            .GroupBy(f => (f.RepositoryId, f.Scanner, Severity: FindingSeverity.For(new FindingEntity { Detector = f.Detector, Verified = f.Verified, UpdateType = f.UpdateType, Advisories = f.Advisories })))
            .Select(g => new CountRow(g.Key.RepositoryId, g.Key.Scanner, g.Key.Severity, g.Count()));
        var verified = (await db.Findings.AsNoTracking().Where(f => f.State == "open" && f.Verified).GroupBy(f => f.RepositoryId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync())
            .Where(v => managed.Contains(v.Id)).ToDictionary(v => v.Id, v => v.Count);
        return (stored.Concat(derived).Where(c => managed.Contains(c.RepositoryId)).ToList(), verified);
    }
}

/// <summary>"What do I fix, and where": open findings grouped into actions a person can take (bump a base image, upgrade a package, rotate a secret…), ranked, plus per-repository risk for list views.</summary>
public static class FixesEndpoints
{
    private static readonly string[] Severities = ["critical", "high", "medium", "low"];
    private static int Rank(string severity) => Array.IndexOf(Severities, severity) is var i and >= 0 ? Severities.Length - i : 0;
    private const int MaxFixes = 50;

    public static void MapFixesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/fixes", async (AegisDbContext db, string[]? repositoryId) => Results.Ok(await BuildAsync(db, repositoryId))).RequireAuthorization(AegisRoles.Viewer);
        api.MapGet("/risk/repositories", async (AegisDbContext db) => Results.Ok(await RiskAsync(db))).RequireAuthorization(AegisRoles.Viewer);
    }

    public sealed record Fix(string Kind, string RepositoryId, string RepositoryName, string Title, string Where, string Action, string Severity, int Critical, int High, int Medium, int Low, int Items, int Fixable, bool Verified, string Scanner);

    private static Fix Make(string kind, string repo, string name, string title, string where, string action, string scanner, IEnumerable<(string Severity, int Count, bool Fixable)> rows, bool verified = false)
    {
        var list = rows.ToList();
        int Of(string s) => list.Where(r => r.Severity == s).Sum(r => r.Count);
        var worst = Severities.FirstOrDefault(s => Of(s) > 0) ?? "low";
        return new Fix(kind, repo, name, title, where, action, worst, Of("critical"), Of("high"), Of("medium"), Of("low"), list.Sum(r => r.Count), list.Where(r => r.Fixable).Sum(r => r.Count), verified, scanner);
    }

    /// <summary>The upgrade advice an SBOM finding carries after " — " ("pkg 1.0 — Upgrade pkg to version 1.2"); empty when no fixed version exists.</summary>
    private static string Advice(string detail) => detail.IndexOf(" — ", StringComparison.Ordinal) is var i and >= 0 ? detail[(i + 3)..] : "";
    private static string Package(string detail) => detail.IndexOf(" — ", StringComparison.Ordinal) is var i and >= 0 ? detail[..i] : detail;

    public static async Task<object> BuildAsync(AegisDbContext db, IReadOnlyCollection<string>? scope)
    {
        var repos = await db.Repositories.AsNoTracking().Where(r => scope == null || scope.Count == 0 || scope.Contains(r.Id)).Select(r => new { r.Id, r.Name }).ToListAsync();
        var names = repos.ToDictionary(r => r.Id, r => r.Name);
        var ids = names.Keys.ToList();
        var fixes = new List<Fix>();

        // SBOM findings: one action per image (nobody patches libssl inside node:12-alpine, they bump the base) and one per vulnerable package in a lock file.
        var sbom = await db.Findings.AsNoTracking().Where(f => f.State == "open" && f.Scanner == "sbom" && ids.Contains(f.RepositoryId))
            .GroupBy(f => new { f.RepositoryId, f.SafePath, f.Detail, f.Severity }).Select(g => new { g.Key.RepositoryId, g.Key.SafePath, g.Key.Detail, g.Key.Severity, Count = g.Count() }).ToListAsync();
        foreach (var image in sbom.Where(s => s.SafePath.StartsWith("image:", StringComparison.Ordinal)).GroupBy(s => (s.RepositoryId, s.SafePath)))
        {
            var rows = image.Select(r => (r.Severity, r.Count, Fixable: Advice(r.Detail).Length > 0)).ToList();
            var fixable = rows.Where(r => r.Fixable).Sum(r => r.Count);
            fixes.Add(Make("image", image.Key.RepositoryId, names[image.Key.RepositoryId], image.Key.SafePath["image:".Length..], "Container image",
                fixable > 0 ? $"Update the base image or rebuild it: {fixable} of {rows.Sum(r => r.Count)} vulnerabilities have a fixed version" : "No fixed versions yet: track the base image for updates", "sbom", rows));
        }
        foreach (var pkg in sbom.Where(s => !s.SafePath.StartsWith("image:", StringComparison.Ordinal)).GroupBy(s => (s.RepositoryId, s.SafePath, s.Detail)))
        {
            var advice = Advice(pkg.Key.Detail);
            fixes.Add(Make("package", pkg.Key.RepositoryId, names[pkg.Key.RepositoryId], Package(pkg.Key.Detail), pkg.Key.SafePath, advice.Length > 0 ? advice : "No fixed version yet", "sbom", pkg.Select(r => (r.Severity, r.Count, advice.Length > 0))));
        }

        // Other scanners with a stored severity (SARIF / Aegis JSON): one action per rule and file.
        var generic = await db.Findings.AsNoTracking().Where(f => f.State == "open" && f.Scanner != "sbom" && f.Severity != "" && ids.Contains(f.RepositoryId))
            .GroupBy(f => new { f.RepositoryId, f.Scanner, f.Detector, f.SafePath, f.Severity }).Select(g => new { g.Key.RepositoryId, g.Key.Scanner, g.Key.Detector, g.Key.SafePath, g.Key.Severity, Count = g.Count() }).ToListAsync();
        foreach (var rule in generic.GroupBy(g => (g.RepositoryId, g.Scanner, g.Detector, g.SafePath)))
            fixes.Add(Make("issue", rule.Key.RepositoryId, names[rule.Key.RepositoryId], rule.Key.Detector.Split('.')[^1].Replace('-', ' '), rule.Key.SafePath, $"Review and fix what {rule.Key.Scanner} reports here", rule.Key.Scanner, rule.Select(r => (r.Severity, r.Count, false))));

        // Built-in scanners (no stored severity): secrets are rotated, dependency updates applied.
        var builtIn = await db.Findings.AsNoTracking().Where(f => f.State == "open" && f.Severity == "" && ids.Contains(f.RepositoryId))
            .Select(f => new { f.RepositoryId, f.Scanner, f.Detector, f.SafePath, f.Detail, f.Verified, f.UpdateType, f.Advisories, f.SourceLine }).ToListAsync();
        foreach (var f in builtIn)
        {
            var severity = FindingSeverity.For(new FindingEntity { Detector = f.Detector, Verified = f.Verified, UpdateType = f.UpdateType, Advisories = f.Advisories });
            var rows = new[] { (severity, 1, false) };
            if (f.Scanner == "trufflehog")
                fixes.Add(Make("secret", f.RepositoryId, names[f.RepositoryId], $"{f.Detector} secret", f.SourceLine is { } l ? $"{f.SafePath}:{l}" : f.Detail.StartsWith("line ", StringComparison.Ordinal) ? $"{f.SafePath}:{f.Detail[5..]}" : f.SafePath,
                    f.Verified ? "Live credential: rotate it now, then remove it from the file and git history" : "Check whether it is real; if so rotate it and remove it from the file and history", "trufflehog", rows, f.Verified));
            else if (f.Detector == "RenovateDependency")
                fixes.Add(Make("update", f.RepositoryId, names[f.RepositoryId], f.Detail, f.SafePath, f.Advisories.Length > 2 ? "Update: the current version has a known vulnerability" : "Update the dependency", "renovate", rows));
            else
                fixes.Add(Make("issue", f.RepositoryId, names[f.RepositoryId], f.Detector, f.SafePath, "Fix the reported problem", f.Scanner, rows));
        }

        var ranked = fixes.OrderByDescending(f => f.Verified).ThenByDescending(f => Rank(f.Severity)).ThenByDescending(f => f.Critical).ThenByDescending(f => f.High).ThenByDescending(f => f.Items).ThenBy(f => f.Title, StringComparer.Ordinal).ToList();
        int Sum(Func<Fix, int> pick) => fixes.Sum(pick);
        return new { total = fixes.Count, shown = Math.Min(MaxFixes, ranked.Count), fixes = ranked.Take(MaxFixes), totals = new { critical = Sum(f => f.Critical), high = Sum(f => f.High), medium = Sum(f => f.Medium), low = Sum(f => f.Low), verified = fixes.Count(f => f.Verified) } };
    }

    /// <summary>Per repository: severity split, verified secrets and the last successful scan, for list views (no CVE pass).</summary>
    public static async Task<object> RiskAsync(AegisDbContext db)
    {
        var repos = await db.Repositories.AsNoTracking().Select(r => new { r.Id, r.Status }).ToListAsync();
        var (counts, verified) = await RiskCounts.LoadAsync(db, repos.Select(r => r.Id).ToHashSet());
        var lastScan = (await db.Executions.AsNoTracking().Where(e => e.Status == "succeeded").GroupBy(e => e.RepositoryId).Select(g => new { Id = g.Key, At = g.Max(e => e.FinishedAt) }).ToListAsync()).ToDictionary(x => x.Id, x => x.At);
        var by = counts.GroupBy(c => c.RepositoryId).ToDictionary(g => g.Key, g => g.ToList());
        return repos.Select(r =>
        {
            var rows = by.GetValueOrDefault(r.Id) ?? [];
            int Of(string s) => rows.Where(c => c.Severity == s).Sum(c => c.Count);
            return new { id = r.Id, blocked = r.Status == "Blocked", verified = verified.GetValueOrDefault(r.Id), critical = Of("critical"), high = Of("high"), medium = Of("medium"), low = Of("low"), lastScanAt = lastScan.GetValueOrDefault(r.Id) };
        }).ToList();
    }
}
