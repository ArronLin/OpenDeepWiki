using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories;

public interface IBranchGenerationTaskService
{
    Task<BranchGenerationTaskResult> EnqueueScanPlanAsync(string repositoryId, string branchId,
        CancellationToken cancellationToken = default);
    Task<BranchGenerationTaskResult> EnqueueFullGenerationAsync(
        string repositoryId,
        string branchId,
        string? requestedBy = null,
        int priority = 100,
        CancellationToken cancellationToken = default);

    Task<BranchGenerationTaskResult> RetryAsync(
        string taskId,
        CancellationToken cancellationToken = default);

    Task<BranchGenerationTaskResult> CancelAsync(
        string taskId,
        CancellationToken cancellationToken = default);

    Task<BranchGenerationTaskResult> PauseAsync(string taskId, CancellationToken cancellationToken = default);
}

public sealed record BranchGenerationTaskResult(
    bool Success,
    BranchGenerationTask? Task = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    RepositoryGenerationLock? ActiveLock = null);

public sealed class BranchGenerationTaskService(
    IContext context,
    IRepositoryGenerationLockService lockService,
    IBranchFullGenerationCleaner branchCleaner) : IBranchGenerationTaskService
{
    public Task<BranchGenerationTaskResult> EnqueueScanPlanAsync(string repositoryId, string branchId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(repositoryId, branchId, BranchGenerationTaskMode.ScanPlan, null, 100, cancellationToken);

    public Task<BranchGenerationTaskResult> EnqueueFullGenerationAsync(string repositoryId, string branchId,
        string? requestedBy = null, int priority = 100, CancellationToken cancellationToken = default) =>
        EnqueueAsync(repositoryId, branchId, BranchGenerationTaskMode.Full, requestedBy, priority, cancellationToken);

    private async Task<BranchGenerationTaskResult> EnqueueAsync(
        string repositoryId,
        string branchId,
        BranchGenerationTaskMode mode,
        string? requestedBy = null,
        int priority = 100,
        CancellationToken cancellationToken = default)
    {
        var repository = await context.Repositories
            .FirstOrDefaultAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken);

        if (repository is null)
        {
            return new BranchGenerationTaskResult(false, ErrorCode: "REPOSITORY_NOT_FOUND", ErrorMessage: "仓库不存在");
        }

        if (repository.SourceType == RepositorySourceType.Documentation)
            return new BranchGenerationTaskResult(false, ErrorCode: "DOCUMENTATION_ONLY",
                ErrorMessage: "导入的知识库没有源码，无法重新生成；现有文档已保留");

        var branch = await context.RepositoryBranches
            .FirstOrDefaultAsync(item => item.Id == branchId && item.RepositoryId == repositoryId && !item.IsDeleted, cancellationToken);

        if (branch is null)
        {
            return new BranchGenerationTaskResult(false, ErrorCode: "BRANCH_NOT_FOUND", ErrorMessage: "分支不存在");
        }

        if (mode == BranchGenerationTaskMode.Full && repository.Status is (RepositoryStatus.Pending or RepositoryStatus.Processing))
        {
            return new BranchGenerationTaskResult(false, ErrorCode: "REPOSITORY_GENERATION_ACTIVE", ErrorMessage: "仓库正在处理中，无法创建 branch 生成任务");
        }

        var activeBranchTask = await FindActiveBranchTaskAsync(repositoryId, branchId, mode, cancellationToken);
        if (activeBranchTask is not null)
        {
            if (mode == BranchGenerationTaskMode.ScanPlan && activeBranchTask.Mode == mode)
                return new BranchGenerationTaskResult(true, activeBranchTask);
            return new BranchGenerationTaskResult(false, activeBranchTask, "BRANCH_GENERATION_ACTIVE", "已有进行中的 branch 生成任务");
        }

        var task = new BranchGenerationTask
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repositoryId,
            BranchId = branchId,
            Status = BranchGenerationTaskStatus.Pending,
            Mode = mode,
            Priority = priority,
            IsManualTrigger = true,
            RequestedBy = requestedBy,
            CreatedAt = DateTime.UtcNow
        };

        await using var transaction = await EfContextTransaction.BeginIfSupportedAsync(context, cancellationToken);
        var lockAcquired = mode == BranchGenerationTaskMode.ScanPlan || await lockService.TryAcquireAsync(
            context,
            repositoryId,
            RepositoryGenerationLockOwnerType.BranchTask,
            task.Id,
            RepositoryGenerationLockScope.Branch,
            cancellationToken);

        if (!lockAcquired)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return new BranchGenerationTaskResult(
                false,
                ErrorCode: "GENERATION_LOCK_CONFLICT",
                ErrorMessage: "仓库已有生成任务正在排队或处理中",
                ActiveLock: await lockService.GetLockAsync(repositoryId, cancellationToken));
        }

        if (mode == BranchGenerationTaskMode.Full)
        {
            branch.GenerationStatus = BranchGenerationTaskStatus.Pending;
            branch.LastGenerationTaskId = task.Id;
            branch.LastGenerationError = null;
            branch.LastGenerationStartedAt = null;
            branch.LastGenerationCompletedAt = null;
            branch.UpdateTimestamp();
        }

        context.BranchGenerationTasks.Add(task);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException) when (mode == BranchGenerationTaskMode.ScanPlan)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            EfContextTransaction.ClearPendingChanges(context);
            var concurrent = await FindActiveBranchTaskAsync(repositoryId, branchId, mode, cancellationToken);
            if (concurrent is not null) return new BranchGenerationTaskResult(true, concurrent);
            throw;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            else
            {
                EfContextTransaction.ClearPendingChanges(context);
                await lockService.ReleaseAsync(
                    context,
                    repositoryId,
                    RepositoryGenerationLockOwnerType.BranchTask,
                    task.Id,
                    CancellationToken.None);
            }

            throw;
        }

        return new BranchGenerationTaskResult(true, task);
    }

    public async Task<BranchGenerationTaskResult> RetryAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await context.BranchGenerationTasks
            .FirstOrDefaultAsync(item => item.Id == taskId && !item.IsDeleted, cancellationToken);

        if (task is null)
        {
            return new BranchGenerationTaskResult(false, ErrorCode: "TASK_NOT_FOUND", ErrorMessage: "任务不存在");
        }

        if (task.Status is not (BranchGenerationTaskStatus.Failed or BranchGenerationTaskStatus.Cancelled or BranchGenerationTaskStatus.Paused))
        {
            return new BranchGenerationTaskResult(false, task, "INVALID_TASK_STATUS", $"当前状态无法继续任务: {task.Status}");
        }

        var activeBranchTask = await FindActiveBranchTaskAsync(task.RepositoryId, task.BranchId, task.Mode, cancellationToken);
        if (activeBranchTask is not null)
        {
            return new BranchGenerationTaskResult(false, activeBranchTask, "BRANCH_GENERATION_ACTIVE", "已有进行中的 branch 生成任务");
        }

        await using var transaction = await EfContextTransaction.BeginIfSupportedAsync(context, cancellationToken);
        var lockAcquired = task.Mode == BranchGenerationTaskMode.ScanPlan || await lockService.TryAcquireAsync(
            context,
            task.RepositoryId,
            RepositoryGenerationLockOwnerType.BranchTask,
            task.Id,
            RepositoryGenerationLockScope.Branch,
            cancellationToken);

        if (!lockAcquired)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return new BranchGenerationTaskResult(
                false,
                task,
                "GENERATION_LOCK_CONFLICT",
                "仓库已有生成任务正在排队或处理中",
                await lockService.GetLockAsync(task.RepositoryId, cancellationToken));
        }

        var branch = await context.RepositoryBranches
            .FirstOrDefaultAsync(item => item.Id == task.BranchId && !item.IsDeleted, cancellationToken);

        if (branch is null)
        {
            return new BranchGenerationTaskResult(false, task, "BRANCH_NOT_FOUND", "分支不存在");
        }

        if (task.Mode == BranchGenerationTaskMode.Full && branch.DeepScanPlanJson is null)
            await branchCleaner.CleanAsync(context, branch, cancellationToken);

        task.Status = BranchGenerationTaskStatus.Pending;
        task.RequestedStopStatus = null;
        task.RetryCount++;
        task.ErrorMessage = null;
        task.StartedAt = null;
        task.CompletedAt = null;
        task.UpdateTimestamp();

        if (task.Mode == BranchGenerationTaskMode.Full)
            branch.LastGenerationTaskId = task.Id;
        context.BranchGenerationTasks.Update(task);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            else
            {
                EfContextTransaction.ClearPendingChanges(context);
                await lockService.ReleaseAsync(
                    context,
                    task.RepositoryId,
                    RepositoryGenerationLockOwnerType.BranchTask,
                    task.Id,
                    CancellationToken.None);
            }

            throw;
        }

        return new BranchGenerationTaskResult(true, task);
    }

    public Task<BranchGenerationTaskResult> CancelAsync(string taskId, CancellationToken cancellationToken = default) =>
        StopAsync(taskId, BranchGenerationTaskStatus.Cancelled, cancellationToken);

    public Task<BranchGenerationTaskResult> PauseAsync(string taskId, CancellationToken cancellationToken = default) =>
        StopAsync(taskId, BranchGenerationTaskStatus.Paused, cancellationToken);

    private async Task<BranchGenerationTaskResult> StopAsync(string taskId, BranchGenerationTaskStatus target,
        CancellationToken cancellationToken)
    {
        var task = await context.BranchGenerationTasks
            .FirstOrDefaultAsync(item => item.Id == taskId && !item.IsDeleted, cancellationToken);

        if (task is null)
        {
            return new BranchGenerationTaskResult(false, ErrorCode: "TASK_NOT_FOUND", ErrorMessage: "任务不存在");
        }

        if (task.Status == BranchGenerationTaskStatus.Processing)
        {
            task.RequestedStopStatus = target;
            task.UpdateTimestamp();
            await context.SaveChangesAsync(cancellationToken);
            return new BranchGenerationTaskResult(true, task);
        }

        if (task.Status != BranchGenerationTaskStatus.Pending)
        {
            return new BranchGenerationTaskResult(false, task, "INVALID_TASK_STATUS", $"当前状态无法停止任务: {task.Status}");
        }

        task.Status = target;
        task.CompletedAt = DateTime.UtcNow;
        task.UpdateTimestamp();

        var branch = await context.RepositoryBranches
            .FirstOrDefaultAsync(item => item.Id == task.BranchId && !item.IsDeleted, cancellationToken);

        if (branch is not null && task.Mode == BranchGenerationTaskMode.Full)
        {
            branch.GenerationStatus = target;
            branch.LastGenerationTaskId = task.Id;
            branch.LastGenerationCompletedAt = task.CompletedAt;
            branch.UpdateTimestamp();
        }

        await lockService.ReleaseAsync(
            context,
            task.RepositoryId,
            RepositoryGenerationLockOwnerType.BranchTask,
            task.Id,
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        return new BranchGenerationTaskResult(true, task);
    }

    private async Task<BranchGenerationTask?> FindActiveBranchTaskAsync(
        string repositoryId,
        string branchId,
        BranchGenerationTaskMode mode,
        CancellationToken cancellationToken)
    {
        return await context.BranchGenerationTasks
            .Where(item => !item.IsDeleted &&
                           item.RepositoryId == repositoryId &&
                           item.Mode == mode && (mode != BranchGenerationTaskMode.ScanPlan || item.BranchId == branchId) &&
                           (item.Status == BranchGenerationTaskStatus.Pending ||
                            item.Status == BranchGenerationTaskStatus.Processing))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
