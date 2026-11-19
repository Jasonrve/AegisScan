using System.Text.RegularExpressions;

namespace AegisScan.KubeOps.Api;

/// <summary>
/// Builds browser links into the forge (GitHub, GitLab, Gitea) that hosts a repository: the repository, a commit, a file at a
/// line, pull/merge requests and issues. <c>overrides</c> maps an internal base URL to the one browsers can reach
/// (e.g. <c>http://gitea:3000</c> -> <c>http://localhost:3300</c>); it is the "forgeUrlOverrides" setting.
/// </summary>
public static partial class ForgeLinks
{
    /// <summary>https://host/owner/repo for https/http/ssh clone URLs (incl. scp-style git@host:owner/repo.git); null if unparseable.</summary>
    public static string? RepoUrl(string cloneUrl, IReadOnlyDictionary<string, string>? overrides = null)
    {
        string? url = null;
        if (Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            var scheme = uri.Scheme is "http" ? "http" : "https";
            url = $"{scheme}://{uri.Authority.Split('@')[^1]}{uri.AbsolutePath}";
        }
        else if (ScpLike().Match(cloneUrl) is { Success: true } m)
            url = $"https://{m.Groups[1].Value}/{m.Groups[2].Value}";
        if (url is null) return null;
        url = url.TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.Ordinal)) url = url[..^4];
        if (overrides is not null)
            foreach (var (from, to) in overrides)
                if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase)) return to.TrimEnd('/') + url[from.Length..];
        return url;
    }

    /// <summary>owner/repo (or group/sub/repo) path of the clone URL, as Renovate expects in RENOVATE_REPOSITORIES.</summary>
    public static string? Slug(string cloneUrl)
    {
        var url = RepoUrl(cloneUrl);
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.Trim('/') : null;
    }

    /// <summary>Scheme and authority of the clone URL, e.g. http://gitea:3000 (no override applied: used from inside the cluster).</summary>
    public static string? Origin(string cloneUrl)
    {
        var url = RepoUrl(cloneUrl);
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}" : null;
    }

    public static string? CommitUrl(string provider, string? repoUrl, string sha) =>
        repoUrl is null || sha.Length == 0 ? null : provider.ToLowerInvariant() switch
        {
            "gitlab" => $"{repoUrl}/-/commit/{sha}",
            _ => $"{repoUrl}/commit/{sha}"
        };

    public static string? FileUrl(string provider, string? repoUrl, string sha, string path, long? line)
    {
        if (repoUrl is null || sha.Length == 0 || path.Length == 0) return null;
        var anchor = line is > 0 ? $"#L{line}" : "";
        var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return provider.ToLowerInvariant() switch
        {
            "gitlab" => $"{repoUrl}/-/blob/{sha}/{encoded}{anchor}",
            "gitea" => $"{repoUrl}/src/commit/{sha}/{encoded}{anchor}",
            _ => $"{repoUrl}/blob/{sha}/{encoded}{anchor}"
        };
    }

    /// <summary>Raw file content URL (for reading a manifest at a commit); GitHub raw content lives on its own host.</summary>
    public static string? RawUrl(string provider, string? repoUrl, string sha, string path)
    {
        if (repoUrl is null || sha.Length == 0 || path.Length == 0) return null;
        var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return provider.ToLowerInvariant() switch
        {
            "gitlab" => $"{repoUrl}/-/raw/{sha}/{encoded}",
            "gitea" => $"{repoUrl}/raw/commit/{sha}/{encoded}",
            _ when Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri) && uri.Host == "github.com" => $"https://raw.githubusercontent.com/{uri.AbsolutePath.Trim('/')}/{sha}/{encoded}",
            _ => $"{repoUrl}/raw/{sha}/{encoded}"
        };
    }

    public static string? PullRequestUrl(string provider, string? repoUrl, int number) =>
        repoUrl is null ? null : provider.ToLowerInvariant() switch
        {
            "gitlab" => $"{repoUrl}/-/merge_requests/{number}",
            "gitea" => $"{repoUrl}/pulls/{number}",
            _ => $"{repoUrl}/pull/{number}"
        };

    public static string? IssueUrl(string provider, string? repoUrl, int number) =>
        repoUrl is null ? null : string.Equals(provider, "gitlab", StringComparison.OrdinalIgnoreCase) ? $"{repoUrl}/-/issues/{number}" : $"{repoUrl}/issues/{number}";

    [GeneratedRegex(@"^[\w.-]+@([\w.-]+):(.+)$")]
    private static partial Regex ScpLike();
}
