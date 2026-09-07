using AegisScan.KubeOps.Data;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Postgres advisory-lock helper so only one replica performs a given periodic task at a time.
/// Needed because ScanExecutionWorker/SchedulingWorker run independently in every replica —
/// without this, more than one replica would create duplicate Jobs or double-queue scans.
/// </summary>
public static class DistributedLock
{
    /// <returns>true if the lock was acquired and <paramref name="action"/> ran; false if another replica holds it.</returns>
    public static async Task<bool> TryRunExclusiveAsync(AegisDbContext db, long lockId, Func<Task> action, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({lockId}) AS \"Value\"").FirstAsync(ct);
        if (!acquired)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }
        await action();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
