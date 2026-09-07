using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AegisScan.KubeOps.Resources;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;

namespace AegisScan.KubeOps.Controllers;

[EntityRbac(typeof(V1RepositorySource), Verbs = RbacVerb.All)]
public sealed class RepositorySourceController(IKubernetesClient client, IHttpClientFactory httpClientFactory, ILogger<RepositorySourceController> logger) : IEntityController<V1RepositorySource>
{
    public async Task<ReconciliationResult<V1RepositorySource>> ReconcileAsync(V1RepositorySource entity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entity.Spec.Provider) || string.IsNullOrWhiteSpace(entity.Spec.Organization))
        {
            entity.Status.Phase = "Error";
            entity.Status.Message = "provider and organization are required.";
            entity = await client.UpdateStatusAsync(entity, cancellationToken);
            return ReconciliationResult<V1RepositorySource>.Success(entity);
        }

        if (!string.Equals(entity.Spec.Provider, "github", StringComparison.OrdinalIgnoreCase))
        {
            entity.Status.Phase = "Ready";
            entity.Status.Message = $"Provider '{entity.Spec.Provider}' discovery is not implemented yet; only 'github' is supported.";
            entity = await client.UpdateStatusAsync(entity, cancellationToken);
            return ReconciliationResult<V1RepositorySource>.Success(entity);
        }

        try
        {
            var repos = await DiscoverGitHubRepositoriesAsync(entity, cancellationToken);
            foreach (var repo in repos)
            {
                await UpsertRepositoryAsync(entity, repo, cancellationToken);
            }
            entity.Status.Phase = "Ready";
            entity.Status.RepositoryCount = repos.Count;
            entity.Status.Message = $"Discovered {repos.Count} repositor{(repos.Count == 1 ? "y" : "ies")}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            entity.Status.Phase = "Error";
            entity.Status.Message = $"Discovery failed: {ex.Message}";
            logger.LogError(ex, "GitHub discovery failed for RepositorySource {Name}.", entity.Metadata.Name);
        }
        entity = await client.UpdateStatusAsync(entity, cancellationToken);
        return ReconciliationResult<V1RepositorySource>.Success(entity);
    }

    public Task<ReconciliationResult<V1RepositorySource>> DeletedAsync(V1RepositorySource entity, CancellationToken cancellationToken) =>
        Task.FromResult(ReconciliationResult<V1RepositorySource>.Success(entity));

    /// <summary>Lists an org's repositories via GitHub's public API. Credential-based auth for private orgs is not wired up yet.</summary>
    private async Task<List<GitHubRepo>> DiscoverGitHubRepositoriesAsync(V1RepositorySource source, CancellationToken ct)
    {
        var httpClient = httpClientFactory.CreateClient("resilient");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AegisScan/1.0");
        var url = $"https://api.github.com/orgs/{Uri.EscapeDataString(source.Spec.Organization)}/repos?per_page=100";
        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<List<GitHubRepo>>(cancellationToken: ct) ?? [];
        return payload.Where(r => (source.Spec.IncludeArchived || !r.Archived) && MatchesFilters(r.Name, source.Spec.Include, source.Spec.Exclude)).ToList();
    }

    private static bool MatchesFilters(string name, List<string> include, List<string> exclude)
    {
        if (exclude.Any(pattern => GlobMatch(pattern, name))) return false;
        return include.Count == 0 || include.Any(pattern => GlobMatch(pattern, name));
    }

    private static bool GlobMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private async Task UpsertRepositoryAsync(V1RepositorySource source, GitHubRepo repo, CancellationToken ct)
    {
        var name = SanitizeName(repo.Name);
        var existing = await client.GetAsync<V1Repository>(name, source.Metadata.NamespaceProperty, ct);
        var desired = existing ?? new V1Repository
        {
            ApiVersion = "aegisscan.io/v1alpha1",
            Kind = "Repository",
            Metadata = new() { Name = name, NamespaceProperty = source.Metadata.NamespaceProperty }
        };
        desired.Spec.Provider = "github";
        desired.Spec.CloneURL = repo.CloneUrl;
        desired.Spec.DefaultBranch = repo.DefaultBranch;
        if (existing is null) await client.CreateAsync(desired, ct);
        else await client.UpdateAsync(desired, ct);
    }

    private static string SanitizeName(string name) => name.ToLowerInvariant().Replace('_', '-').Replace('.', '-');

    private sealed record GitHubRepo(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("clone_url")] string CloneUrl,
        [property: JsonPropertyName("default_branch")] string DefaultBranch,
        [property: JsonPropertyName("archived")] bool Archived);
}
