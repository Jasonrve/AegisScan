using KubeOps.Abstractions.Entities;
using k8s.Models;

namespace AegisScan.KubeOps.Resources;

[KubernetesEntity(Group = "aegisscan.io", ApiVersion = "v1alpha1", Kind = "RepositorySource", PluralName = "repositorysources")]
public sealed class V1RepositorySource : CustomKubernetesEntity<V1RepositorySource.Specification, V1RepositorySource.StatusData>
{
    public sealed class Specification { public string Provider { get; set; } = ""; public string Organization { get; set; } = ""; public List<string> Include { get; set; } = []; public List<string> Exclude { get; set; } = []; public bool IncludeArchived { get; set; } public string? Schedule { get; set; } public string? CredentialSecretRef { get; set; } }
    public sealed class StatusData { public string Phase { get; set; } = "Pending"; public int RepositoryCount { get; set; } public string Message { get; set; } = ""; }
}
