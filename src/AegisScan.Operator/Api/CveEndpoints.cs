using System.Text.Json;
using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Api;

public sealed record CveRating(string Source, double? Score, string Severity, string Method, string Vector);
public sealed record CveInfo(string Id, double? Score, string Method, string Vector, string Description, string Published, string Updated, int[] Cwes, string[] References, CveRating[] Ratings);

/// <summary>
/// CVE scores and descriptions, read from the vulnerabilities of the stored CycloneDX SBOMs (only the "sbom" scanner records them; other scanners' CVE findings have a severity but no score).
/// Parsing every document is slow, so the result is cached until any SBOM changes.
/// shortcut: one process-wide cache rebuilt on any SBOM change, move the scores onto the findings at scan time if the SBOM set gets large.
/// </summary>
internal static class CveCatalog
{
    private static (string Key, Dictionary<string, CveInfo> Map)? cache;

    public static async Task<Dictionary<string, CveInfo>> LoadAsync(AegisDbContext db, CancellationToken ct)
    {
        var stamps = await db.Sboms.AsNoTracking().Where(s => s.Document != "").Select(s => new { s.Id, s.GeneratedAt }).OrderBy(s => s.Id).ToListAsync(ct);
        var key = string.Join('|', stamps.Select(s => s.Id + s.GeneratedAt.UtcTicks));
        if (cache is { } hit && hit.Key == key) return hit.Map;
        var map = new Dictionary<string, CveInfo>(StringComparer.Ordinal);
        foreach (var stamp in stamps)
        {
            var json = await db.Sboms.AsNoTracking().Where(s => s.Id == stamp.Id).Select(s => s.Document).FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(json)) continue;
            try { using var doc = JsonDocument.Parse(json); Read(doc.RootElement, map); }
            catch (JsonException) { /* a corrupt document just contributes nothing */ }
        }
        cache = (key, map);
        return map;
    }

    private static void Read(JsonElement root, Dictionary<string, CveInfo> map)
    {
        if (!root.TryGetProperty("vulnerabilities", out var vulns) || vulns.ValueKind != JsonValueKind.Array) return;
        foreach (var v in vulns.EnumerateArray())
        {
            var id = Str(v, "id");
            if (id.Length == 0) continue;
            var ratings = new List<CveRating>();
            if (v.TryGetProperty("ratings", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray())
                    ratings.Add(new CveRating(GenericResultParser.Cap(Str(r, "source", "name"), 60), r.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : null,
                        Str(r, "severity").ToLowerInvariant(), Str(r, "method"), GenericResultParser.Cap(Str(r, "vector"), 200)));
            var best = ratings.Where(r => r.Score is not null).OrderByDescending(r => r.Score).FirstOrDefault();
            var refs = new List<string>();
            if (v.TryGetProperty("advisories", out var adv) && adv.ValueKind == JsonValueKind.Array)
                refs.AddRange(adv.EnumerateArray().Select(a => GenericResultParser.SafeUrl(Str(a, "url"))).Where(u => !string.IsNullOrEmpty(u))!);
            var cwes = v.TryGetProperty("cwes", out var cw) && cw.ValueKind == JsonValueKind.Array ? cw.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToArray() : [];
            var info = new CveInfo(id, best?.Score, best?.Method ?? "", best?.Vector ?? "", GenericResultParser.Cap(Str(v, "description"), 2000), Str(v, "published"), Str(v, "updated"),
                cwes, refs.Distinct().Take(10).ToArray(), ratings.Take(12).ToArray());
            if (!map.TryGetValue(id, out var old) || (info.Score ?? -1) > (old.Score ?? -1)) map[id] = info;
        }
    }

    private static string Str(JsonElement element, params string[] path)
    {
        foreach (var key in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element)) return "";
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";
    }
}

/// <summary>The CVE view: every open CVE with its score, which ones are new, and for one CVE the full detail with every affected repository.</summary>
public static class CveEndpoints
{
    private static readonly string[] Severities = ["critical", "high", "medium", "low"];
    private static int Rank(string severity) => Array.IndexOf(Severities, severity) is var i and >= 0 ? Severities.Length - i : 0;
    private static string AdvisoryUrl(string id) => id.StartsWith("GHSA-", StringComparison.Ordinal) ? $"https://github.com/advisories/{id}" : $"https://nvd.nist.gov/vuln/detail/{id}";

    /// <summary>Finding detail is "package version" (SBOM scanner: plus " — recommendation").</summary>
    public static (string Package, string Version, string Fix) SplitDetail(string detail)
    {
        var cut = detail.IndexOf(" — ", StringComparison.Ordinal);
        var left = cut >= 0 ? detail[..cut] : detail;
        var fix = cut >= 0 ? detail[(cut + 3)..] : "";
        var space = left.LastIndexOf(' ');
        return space > 0 ? (left[..space], left[(space + 1)..], fix) : (left, "", fix);
    }

    public static void MapCveApi(this RouteGroupBuilder api)
    {
        api.MapGet("/cves", async (AegisDbContext db, CancellationToken ct, int days = 7, string? severity = null, string? search = null, bool newOnly = false) =>
        {
            days = Math.Clamp(days, 1, 365);
            var managed = await db.Repositories.AsNoTracking().Select(r => r.Id).ToHashSetAsync(ct);
            var (open, fresh) = await DashboardEndpoints.LoadCvesAsync(db, managed, DateTimeOffset.UtcNow.AddDays(-days));
            var freshIds = fresh.Select(f => f.Detector).ToHashSet();
            var catalog = await CveCatalog.LoadAsync(db, ct);
            var rows = open.GroupBy(f => f.Detector).Select(g =>
            {
                catalog.TryGetValue(g.Key, out var info);
                var packages = g.Select(f => SplitDetail(f.Detail).Package).Distinct().ToList();
                return new
                {
                    id = g.Key,
                    severity = g.Select(f => f.Severity).OrderByDescending(Rank).First(),
                    score = info?.Score,
                    vector = info?.Vector ?? "",
                    summary = GenericResultParser.Cap(info?.Description ?? "", 200),
                    repos = g.Select(f => f.RepositoryId).Distinct().Count(),
                    occurrences = g.Count(),
                    packages = packages.Take(3).ToList(),
                    packageCount = packages.Count,
                    fixAvailable = g.Any(f => f.Detail.Contains(" — Upgrade", StringComparison.Ordinal)),
                    firstSeen = g.Min(f => f.FirstSeenAt),
                    isNew = freshIds.Contains(g.Key)
                };
            }).ToList();
            var filtered = rows.Where(r => (!newOnly || r.isNew) && (string.IsNullOrWhiteSpace(severity) || r.severity == severity)
                && (string.IsNullOrWhiteSpace(search) || r.id.Contains(search, StringComparison.OrdinalIgnoreCase) || r.packages.Any(p => p.Contains(search, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(r => r.isNew).ThenByDescending(r => Rank(r.severity)).ThenByDescending(r => r.score ?? 0).ThenByDescending(r => r.repos).ThenBy(r => r.id, StringComparer.Ordinal).ToList();
            return Results.Ok(new { days, total = filtered.Count, newCount = rows.Count(r => r.isNew), items = filtered.Take(500) });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/cves/{id}", async (string id, AegisDbContext db, CancellationToken ct, int days = 7) =>
        {
            var findings = await db.Findings.AsNoTracking().Where(f => f.State == "open" && f.Detector == id).ToListAsync(ct);
            var catalog = await CveCatalog.LoadAsync(db, ct);
            catalog.TryGetValue(id, out var info);
            if (findings.Count == 0 && info is null) return Results.NotFound();
            var repoIds = findings.Select(f => f.RepositoryId).Distinct().ToList();
            var repos = await db.Repositories.AsNoTracking().Where(r => repoIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            var (_, fresh) = await DashboardEndpoints.LoadCvesAsync(db, repos.Keys.ToHashSet(), DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365)));
            return Results.Ok(new
            {
                id,
                severity = findings.Select(FindingSeverity.For).OrderByDescending(Rank).FirstOrDefault() ?? info?.Ratings.Select(r => r.Severity).OrderByDescending(Rank).FirstOrDefault() ?? "",
                score = info?.Score, method = info?.Method ?? "", vector = info?.Vector ?? "", description = info?.Description ?? "", published = info?.Published ?? "", updated = info?.Updated ?? "",
                cwes = info?.Cwes ?? [], references = info?.References ?? [], ratings = info?.Ratings ?? [], advisoryUrl = AdvisoryUrl(id),
                isNew = fresh.Any(f => f.Detector == id),
                firstSeen = findings.Count > 0 ? findings.Min(f => f.FirstSeenAt) : (DateTimeOffset?)null,
                fixAvailable = findings.Any(f => f.Detail.Contains(" — Upgrade", StringComparison.Ordinal)),
                occurrences = findings.Count,
                repos = findings.GroupBy(f => f.RepositoryId).Where(g => repos.ContainsKey(g.Key)).Select(g => new
                {
                    id = g.Key, name = repos[g.Key].Name, provider = repos[g.Key].Provider, status = repos[g.Key].Status,
                    findings = g.OrderBy(f => f.SafePath, StringComparer.Ordinal).Select(f =>
                    {
                        var (package, version, fix) = SplitDetail(f.Detail);
                        return new { fingerprint = f.Fingerprint, package, version, fix, path = f.SafePath, scanner = f.Scanner, assignee = f.Assignee, firstSeen = f.FirstSeenAt, commit = f.CommitSHA };
                    }).ToList()
                }).OrderBy(r => r.name, StringComparer.Ordinal).ToList()
            });
        }).RequireAuthorization(AegisRoles.Viewer);
    }
}
