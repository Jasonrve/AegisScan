using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Resources;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using System.Diagnostics.CodeAnalysis;

namespace AegisScan.KubeOps.Controllers;

[EntityRbac(typeof(V1Repository), Verbs = RbacVerb.All)]
public sealed class RepositoryController(IKubernetesClient client, IServiceScopeFactory scopeFactory, ILogger<RepositoryController> logger) : IEntityController<V1Repository>
{
    public async Task<ReconciliationResult<V1Repository>> ReconcileAsync(V1Repository entity, CancellationToken cancellationToken)
    {
        if (entity.Spec.Disabled)
        {
            entity.Status.Phase = "Disabled";
            entity.Status.CacheState = "Ready";
            entity.Status.LatestScanState = "Disabled";
        }
        else if (!IsValid(entity.Spec, out var validationMessage))
        {
            entity.Status.Phase = "Error";
            entity.Status.CacheState = "Unknown";
            entity.Status.LatestScanState = "Blocked";
            entity.Status.Conditions = [new() { Type = "Reconciled", Status = "False", Reason = "InvalidSpec", Message = validationMessage }];
            entity = await client.UpdateStatusAsync(entity, cancellationToken);
            return ReconciliationResult<V1Repository>.Success(entity);
        }
        else
        {
            entity.Status.Phase = "Ready";
            entity.Status.CacheState = "Ready";
            entity.Status.LatestScanState = "Pending";
        }
        entity.Status.Conditions = [new() { Type = "Reconciled", Status = "True", Reason = "DesiredStateApplied", Message = "Repository specification accepted by KubeOps." }];
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Reconciled repository {Name} in namespace {Namespace}", entity.Metadata.Name, entity.Metadata.NamespaceProperty);
        }
        entity = await client.UpdateStatusAsync(entity, cancellationToken);

        await SyncToDatabaseAsync(entity, cancellationToken);
        return ReconciliationResult<V1Repository>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Repository>> DeletedAsync(V1Repository entity, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
        var row = await db.Repositories.FindAsync([entity.Metadata.Name], cancellationToken);
        if (row is not null)
        {
            db.RemoveRepositoryWithData(row);
            await db.SaveChangesAsync(cancellationToken);
        }
        return ReconciliationResult<V1Repository>.Success(entity);
    }

    /// <summary>
    /// CRDs express desired state; the HTTP API and scan pipeline operate on Postgres. This mirrors
    /// each reconciled CRD into a Postgres row (keyed by CRD name) so CRD-declared repositories —
    /// created directly or by RepositorySource discovery — are actually scannable.
    /// </summary>
    private async Task SyncToDatabaseAsync(V1Repository entity, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
        var id = entity.Metadata.Name;
        var row = await db.Repositories.FindAsync([id], ct);
        if (row is null)
        {
            row = new RepositoryEntity { Id = id };
            db.Repositories.Add(row);
        }
        row.Name = id;
        row.Provider = entity.Spec.Provider;
        row.CloneUrl = entity.Spec.CloneURL;
        row.DefaultBranch = entity.Spec.DefaultBranch;
        row.PolicyRef = entity.Spec.PolicyRef;
        // Blocked is a security decision made by a scan: reconciling the CRD (which only knows Ready/Disabled/Error) must never silently lift it.
        if (!(row.Status == "Blocked" && entity.Status.Phase == "Ready")) row.Status = entity.Status.Phase;
        await db.SaveChangesAsync(ct);
    }

    private static bool IsValid(V1Repository.Specification spec, [NotNullWhen(false)] out string? message)
    {
        if (string.IsNullOrWhiteSpace(spec.Provider) || string.IsNullOrWhiteSpace(spec.CloneURL))
        {
            message = "provider and cloneURL are required.";
            return false;
        }
        if (!Uri.TryCreate(spec.CloneURL, UriKind.Absolute, out var cloneUri) || cloneUri.Host.Length == 0 || (cloneUri.Scheme != "https" && cloneUri.Scheme != "ssh"))
        {
            message = "cloneURL must be an absolute https or ssh URL.";
            return false;
        }
        message = null;
        return true;
    }
}
