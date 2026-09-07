using KubeOps.Abstractions.Entities;
using k8s.Models;

namespace AegisScan.KubeOps.Resources;

[KubernetesEntity(Group = "aegisscan.io", ApiVersion = "v1alpha1", Kind = "Repository", PluralName = "repositories")]
public sealed class V1Repository : CustomKubernetesEntity<V1Repository.Specification, V1Repository.StatusData>
{
    public sealed class Specification
    {
        public string Provider { get; set; } = "";
        public string CloneURL { get; set; } = "";
        public string DefaultBranch { get; set; } = "main";
        public bool Disabled { get; set; }
        public List<string> Scanners { get; set; } = [];
        public string? PolicyRef { get; set; }
    }
    public sealed class StatusData
    {
        public string Phase { get; set; } = "Pending";
        public string LatestCommitSHA { get; set; } = "";
        public string CacheState { get; set; } = "Unknown";
        public string LatestScanState { get; set; } = "Pending";
        public List<Condition> Conditions { get; set; } = [];
    }
    public sealed class Condition { public string Type { get; set; } = ""; public string Status { get; set; } = ""; public string Reason { get; set; } = ""; public string Message { get; set; } = ""; }
}
