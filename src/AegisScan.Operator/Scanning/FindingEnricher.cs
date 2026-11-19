using System.Collections.Concurrent;
using System.Text.Json;
using AegisScan.KubeOps.Api;
using AegisScan.KubeOps.Data;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Best-effort enrichment of Renovate dependency findings after a scan: the manifest line of each dependency (read from the forge at the
/// scanned commit) and known vulnerabilities of its current version (OSV.dev, no token needed). Every failure is swallowed — enrichment must
/// never fail a scan — and the vulnerability lookup is switched off with the "vulnerabilityLookup" setting (e.g. air-gapped installs).
/// </summary>
public static class FindingEnricher
{
    private const int MaxDependencies = 100;
    private static readonly Dictionary<string, string> Ecosystems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["npm"] = "npm", ["pypi"] = "PyPI", ["maven"] = "Maven", ["go"] = "Go", ["crate"] = "crates.io", ["nuget"] = "NuGet",
        ["rubygems"] = "RubyGems", ["packagist"] = "Packagist", ["github-tags"] = "GitHub Actions", ["github-releases"] = "GitHub Actions"
    };
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string Json)> VulnerabilityCache = new();

    /// <summary>Returns a warning for the run output when the vulnerability lookup could not complete (so "no advisories" is never mistaken for "lookup failed").</summary>
    public static async Task<string?> EnrichAsync(AegisDbContext db, IHttpClientFactory httpClientFactory, RepositoryEntity repository, string? commit,
        IReadOnlyList<DependencyInfo>? dependencies, CancellationToken ct)
    {
        if (dependencies is not { Count: > 0 }) return null;
        var lookupVulnerabilities = !(await db.Settings.FindAsync(["vulnerabilityLookup"], ct) is { Value: "false" });
        var manifests = new Dictionary<string, string?>();
        var vulnerabilityLookupFailed = false;
        var client = httpClientFactory.CreateClient("resilient");
        var osvQueried = 0;

        foreach (var dep in dependencies.Take(MaxDependencies))
        {
            var finding = await db.Findings.FindAsync([dep.Fingerprint], ct);
            if (finding is null) continue;

            if (commit is not null)
            {
                if (!manifests.TryGetValue(dep.PackageFile, out var content))
                    manifests[dep.PackageFile] = content = await FetchManifestAsync(repository, commit, dep.PackageFile, ct);
                if (content is not null && FindLine(content, dep.Name) is > 0 and var line) finding.SourceLine = line;
            }

            if (lookupVulnerabilities && !vulnerabilityLookupFailed && Ecosystems.TryGetValue(dep.Datasource, out var ecosystem) && dep.Version.Length > 0)
            {
                osvQueried++;
                var json = await QueryOsvAsync(client, ecosystem, dep.Name, dep.Version, ct);
                if (json is null) vulnerabilityLookupFailed = true; // offline / blocked: stop asking for the rest of this scan
                else finding.Advisories = AdvisoriesJson(json);
            }
        }
        return vulnerabilityLookupFailed ? "warning: the vulnerability lookup (api.osv.dev) failed or was blocked; dependency severities do not reflect known vulnerabilities for this run." : null;
    }

    /// <summary>1-based line of the first mention of the dependency in the manifest (a quoted JSON key wins over a bare mention); 0 if absent.</summary>
    public static int FindLine(string content, string name)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++) if (lines[i].Contains($"\"{name}\"", StringComparison.Ordinal)) return i + 1;
        for (var i = 0; i < lines.Length; i++) if (lines[i].Contains(name, StringComparison.Ordinal)) return i + 1;
        return 0;
    }

    /// <summary>Reduces an OSV query response to at most five {id, summary, url} entries (stored as JSON); "" when there are none.</summary>
    public static string AdvisoriesJson(string osvResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(osvResponse);
            if (!doc.RootElement.TryGetProperty("vulns", out var vulns) || vulns.ValueKind != JsonValueKind.Array || vulns.GetArrayLength() == 0) return "";
            var items = vulns.EnumerateArray().Take(5).Select(v =>
            {
                var id = v.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                var summary = v.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                return new { id, summary = summary.Length > 200 ? summary[..200] + "…" : summary, url = $"https://osv.dev/vulnerability/{Uri.EscapeDataString(id)}" };
            }).Where(x => x.id.Length > 0).ToList();
            return items.Count == 0 ? "" : JsonSerializer.Serialize(items);
        }
        catch (JsonException) { return ""; }
    }

    // Manifests are fetched from the forge the repository lives on and nowhere else: no redirects, no host other than the clone URL's own
    // (or GitHub's raw host for github.com), no ".." path tricks, and a hard 1 MB read limit.
    private static readonly HttpClient ManifestClient = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private const int MaxManifestBytes = 1_000_000;

    /// <summary>The only URL a manifest may be fetched from, or null: no traversal segments or backslashes, the repository's own host (or GitHub's raw host), http(s) only.</summary>
    public static Uri? ManifestUri(RepositoryEntity repository, string commit, string packageFile)
    {
        if (packageFile.Split('/').Any(segment => segment is ".." or ".") || packageFile.Contains('\\')) return null;
        var url = ForgeLinks.RawUrl(repository.Provider, ForgeLinks.RepoUrl(repository.CloneUrl), commit, packageFile);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Uri.TryCreate(ForgeLinks.RepoUrl(repository.CloneUrl), UriKind.Absolute, out var repoUri)) return null;
        var allowedHost = uri.Host == repoUri.Host || (repoUri.Host == "github.com" && uri.Host == "raw.githubusercontent.com");
        return allowedHost && uri.Scheme is "http" or "https" ? uri : null;
    }

    private static async Task<string?> FetchManifestAsync(RepositoryEntity repository, string commit, string packageFile, CancellationToken ct)
    {
        if (ManifestUri(repository, commit, packageFile) is not { } uri) return null;
        try
        {
            // HttpClient.Timeout stops applying once headers arrive, so the whole fetch (headers + body) shares one deadline: a trickling
            // body must not hold the worker (and its advisory lock) open.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10)); ct = deadline.Token;
            using var response = await ManifestClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxManifestBytes + 1];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct) is > 0 and var n) read += n;
            return read > MaxManifestBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return null; }
    }

    private static async Task<string?> QueryOsvAsync(HttpClient client, string ecosystem, string name, string version, CancellationToken ct)
    {
        var key = $"{ecosystem}/{name}@{version}";
        if (VulnerabilityCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromHours(6)) return cached.Json;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var body = JsonSerializer.Serialize(new { package = new { name, ecosystem }, version });
            using var response = await client.PostAsync("https://api.osv.dev/v1/query", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            VulnerabilityCache[key] = (DateTimeOffset.UtcNow, json);
            return json;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }
}
