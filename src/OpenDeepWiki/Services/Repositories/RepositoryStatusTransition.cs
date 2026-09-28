using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Applies a conditional repository status transition: the update only takes
/// effect when the row is currently in one of the allowed statuses. Returns the
/// number of affected rows so callers can abandon their work when a concurrent
/// actor (user control request, worker) has already changed the status.
/// </summary>
internal static class RepositoryStatusTransition
{
    public static async Task<int> TryTransitionAsync(
        IContext context,
        string repositoryId,
        RepositoryStatus[] allowedStatuses,
        RepositoryStatus targetStatus,
        CancellationToken cancellationToken)
    {
        if (EfContextCapabilities.SupportsExecuteUpdate(context))
        {
            var now = DateTime.UtcNow;
            var affected = await context.Repositories
                .Where(item => item.Id == repositoryId && !item.IsDeleted && allowedStatuses.Contains(item.Status))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, targetStatus)
                        .SetProperty(item => item.UpdatedAt, now),
                    cancellationToken);

            if (affected == 1)
            {
                // Keep the change tracker in sync so a later SaveChanges on this
                // context cannot overwrite the committed status with a stale value.
                // Reload reads the committed row back; assigning the new values and
                // then marking the entry Unchanged instead reverts in-memory values
                // to their original snapshot, which would resurrect the old status.
                var tracked = context.Repositories.Local.FirstOrDefault(item => item.Id == repositoryId);
                if (tracked is not null && context is DbContext dbContext)
                {
                    await dbContext.Entry(tracked).ReloadAsync(cancellationToken);
                }
            }

            return affected;
        }

        var repository = await context.Repositories
            .FirstOrDefaultAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken);

        if (repository is null || !allowedStatuses.Contains(repository.Status))
        {
            return 0;
        }

        repository.Status = targetStatus;
        repository.UpdateTimestamp();
        await context.SaveChangesAsync(cancellationToken);
        return 1;
    }
}
