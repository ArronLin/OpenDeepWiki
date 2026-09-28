using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories;

public enum RepositoryProcessingControlOutcome
{
    Success = 0,
    NotFound = 1,
    InvalidState = 2,
    Conflict = 3,
    ReleaseWaitTimedOut = 4
}

public sealed record RepositoryProcessingControlResult(
    RepositoryProcessingControlOutcome Outcome,
    RepositoryStatus? CurrentStatus,
    string? Message)
{
    public bool Succeeded => Outcome == RepositoryProcessingControlOutcome.Success;
}

public interface IRepositoryProcessingControlService
{
    Task<RepositoryProcessingControlResult> PauseAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken = default);

    Task<RepositoryProcessingControlResult> ResumeAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken = default);

    Task<RepositoryProcessingControlResult> StopAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken = default);

    Task<RepositoryProcessingControlResult> RestartAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken = default);
}

public sealed class RepositoryProcessingControlService : IRepositoryProcessingControlService
{
    private static readonly RepositoryStatus[] PausableStatuses =
        [RepositoryStatus.Pending, RepositoryStatus.Processing];

    private static readonly RepositoryStatus[] ResumableStatuses =
        [RepositoryStatus.Paused];

    private static readonly RepositoryStatus[] StoppableStatuses =
        [RepositoryStatus.Pending, RepositoryStatus.Processing, RepositoryStatus.Paused];

    private readonly IRepositoryFullRegenerationCleaner _fullRegenerationCleaner;
    private readonly TimeSpan _releaseWaitInterval;
    private readonly TimeSpan _releaseWaitTimeout;

    public RepositoryProcessingControlService(
        IRepositoryFullRegenerationCleaner fullRegenerationCleaner,
        TimeSpan? releaseWaitInterval = null,
        TimeSpan? releaseWaitTimeout = null)
    {
        _fullRegenerationCleaner = fullRegenerationCleaner;
        _releaseWaitInterval = releaseWaitInterval ?? TimeSpan.FromSeconds(1);
        _releaseWaitTimeout = releaseWaitTimeout ?? TimeSpan.FromSeconds(10);
    }

    public Task<RepositoryProcessingControlResult> PauseAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(context, repositoryId, "暂停", PausableStatuses, RepositoryStatus.Paused, cancellationToken);
    }

    public Task<RepositoryProcessingControlResult> ResumeAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(context, repositoryId, "继续", ResumableStatuses, RepositoryStatus.Pending, cancellationToken);
    }

    public Task<RepositoryProcessingControlResult> StopAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(context, repositoryId, "停止", StoppableStatuses, RepositoryStatus.Stopped, cancellationToken);
    }

    public async Task<RepositoryProcessingControlResult> RestartAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        var currentStatus = await ReadStatusAsync(context, repositoryId, cancellationToken);
        if (currentStatus is null)
        {
            return NotFound(repositoryId);
        }

        if (currentStatus == RepositoryStatus.Processing)
        {
            var stopped = await RepositoryStatusTransition.TryTransitionAsync(
                context,
                repositoryId,
                [RepositoryStatus.Processing],
                RepositoryStatus.Stopped,
                cancellationToken);

            if (stopped == 1)
            {
                var released = await WaitForLeaseReleaseAsync(context, repositoryId, cancellationToken);
                if (!released)
                {
                    return new RepositoryProcessingControlResult(
                        RepositoryProcessingControlOutcome.ReleaseWaitTimedOut,
                        RepositoryStatus.Stopped,
                        "已停止当前处理，但生成槽位尚未释放，请稍后重试重新开始");
                }
            }
        }

        return await ClearAndRequeueAsync(context, repositoryId, cancellationToken);
    }

    private async Task<RepositoryProcessingControlResult> TransitionAsync(
        IContext context,
        string repositoryId,
        string operation,
        RepositoryStatus[] allowedStatuses,
        RepositoryStatus targetStatus,
        CancellationToken cancellationToken)
    {
        var currentStatus = await ReadStatusAsync(context, repositoryId, cancellationToken);
        if (currentStatus is null)
        {
            return NotFound(repositoryId);
        }

        if (!allowedStatuses.Contains(currentStatus.Value))
        {
            return new RepositoryProcessingControlResult(
                RepositoryProcessingControlOutcome.InvalidState,
                currentStatus,
                $"当前状态为 {currentStatus}，无法执行{operation}操作");
        }

        var affected = await RepositoryStatusTransition.TryTransitionAsync(context, repositoryId, allowedStatuses, targetStatus, cancellationToken);
        if (affected == 0)
        {
            var racedStatus = await ReadStatusAsync(context, repositoryId, cancellationToken);
            return new RepositoryProcessingControlResult(
                RepositoryProcessingControlOutcome.Conflict,
                racedStatus,
                $"{operation}操作未生效，仓库状态已发生变化");
        }

        return new RepositoryProcessingControlResult(
            RepositoryProcessingControlOutcome.Success,
            targetStatus,
            null);
    }

    private async Task<RepositoryProcessingControlResult> ClearAndRequeueAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        var repository = await context.Repositories
            .FirstOrDefaultAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken);

        if (repository is null)
        {
            return NotFound(repositoryId);
        }

        if (repository.Status == RepositoryStatus.Processing)
        {
            return new RepositoryProcessingControlResult(
                RepositoryProcessingControlOutcome.Conflict,
                repository.Status,
                "仓库正在处理中，请重试重新开始以先停止当前处理");
        }

        var originalStatus = repository.Status;

        await _fullRegenerationCleaner.CleanAsync(context, repository, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var affected = await RepositoryStatusTransition.TryTransitionAsync(
            context,
            repositoryId,
            [originalStatus],
            RepositoryStatus.Pending,
            cancellationToken);

        if (affected == 0)
        {
            var racedStatus = await ReadStatusAsync(context, repositoryId, cancellationToken);
            return new RepositoryProcessingControlResult(
                RepositoryProcessingControlOutcome.Conflict,
                racedStatus,
                "重新开始操作未生效，仓库状态已发生变化");
        }

        return new RepositoryProcessingControlResult(
            RepositoryProcessingControlOutcome.Success,
            RepositoryStatus.Pending,
            null);
    }

    private async Task<bool> WaitForLeaseReleaseAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (await IsLeaseReleasedAsync(context, repositoryId, cancellationToken))
            {
                return true;
            }

            if (stopwatch.Elapsed >= _releaseWaitTimeout)
            {
                return false;
            }

            await Task.Delay(_releaseWaitInterval, cancellationToken);
        }
    }

    private static async Task<bool> IsLeaseReleasedAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        var lockBound = await context.RepositoryGenerationLocks
            .AsNoTracking()
            .AnyAsync(item => item.RepositoryId == repositoryId && !item.IsDeleted && item.InstanceId != null, cancellationToken);

        if (lockBound)
        {
            return false;
        }

        var slotBound = await context.WikiGenerationSlots
            .AsNoTracking()
            .AnyAsync(item => item.RepositoryId == repositoryId && !item.IsDeleted && item.InstanceId != null, cancellationToken);

        return !slotBound;
    }

    private static Task<RepositoryStatus?> ReadStatusAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        return context.Repositories
            .AsNoTracking()
            .Where(item => item.Id == repositoryId && !item.IsDeleted)
            .Select(item => (RepositoryStatus?)item.Status)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static RepositoryProcessingControlResult NotFound(string repositoryId)
    {
        return new RepositoryProcessingControlResult(
            RepositoryProcessingControlOutcome.NotFound,
            null,
            $"仓库 {repositoryId} 不存在");
    }
}
