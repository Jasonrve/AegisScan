using AegisScan.KubeOps.Resources;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;

namespace AegisScan.KubeOps.Controllers;

[EntityRbac(typeof(V1ScanPolicy), Verbs = RbacVerb.All)]
public sealed class ScanPolicyController(IKubernetesClient client) : IEntityController<V1ScanPolicy>
{
    public async Task<ReconciliationResult<V1ScanPolicy>> ReconcileAsync(V1ScanPolicy entity, CancellationToken cancellationToken)
    {
        var valid = entity.Spec.Scanners.Count > 0 && entity.Spec.TimeoutSeconds > 0 && entity.Spec.Retries is >= 0 and <= 10 && entity.Spec.MaxConcurrency > 0;
        entity.Status.Phase = valid ? "Ready" : "Error";
        entity.Status.Message = valid ? "Scan policy accepted." : "scanners, timeoutSeconds, retries, and maxConcurrency must be valid.";
        entity = await client.UpdateStatusAsync(entity, cancellationToken);
        return ReconciliationResult<V1ScanPolicy>.Success(entity);
    }
    public Task<ReconciliationResult<V1ScanPolicy>> DeletedAsync(V1ScanPolicy entity, CancellationToken cancellationToken) =>
        Task.FromResult(ReconciliationResult<V1ScanPolicy>.Success(entity));
}
