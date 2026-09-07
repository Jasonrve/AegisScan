using KubeOps.Abstractions.Entities;
using k8s.Models;

namespace AegisScan.KubeOps.Resources;

[KubernetesEntity(Group = "aegisscan.io", ApiVersion = "v1alpha1", Kind = "ScanPolicy", PluralName = "scanpolicies")]
public sealed class V1ScanPolicy : CustomKubernetesEntity<V1ScanPolicy.Specification, V1ScanPolicy.StatusData>
{
    public sealed class Specification { public List<string> Scanners { get; set; } = []; public bool IncrementalByDefault { get; set; } = true; public string? FullScanSchedule { get; set; } public string? Schedule { get; set; } public int TimeoutSeconds { get; set; } = 1800; public int Retries { get; set; } = 2; public int MaxConcurrency { get; set; } = 4; }
    public sealed class StatusData { public string Phase { get; set; } = "Pending"; public string Message { get; set; } = ""; }
}
