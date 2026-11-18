using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models.Admin;
using OpenDeepWiki.Services.Admin;
using OpenDeepWiki.Services.Graphify;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Endpoints.Admin;

/// <summary>
/// 管理端仓库管理端点
/// </summary>
public static class AdminRepositoryEndpoints
{
    public static RouteGroupBuilder MapAdminRepositoryEndpoints(this RouteGroupBuilder group)
    {
        var repoGroup = group.MapGroup("/repositories")
            .WithTags("管理端-仓库管理");

        // 获取仓库列表（分页）
        repoGroup.MapGet("/", async (
            [FromQuery] int page,
            [FromQuery] int pageSize,
            [FromQuery] string? search,
            [FromQuery] int? status,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 20;
            var result = await repositoryService.GetRepositoriesAsync(page, pageSize, search, status);
            return Results.Ok(new { success = true, data = result });
        })
        .WithName("AdminGetRepositories")
        .WithSummary("获取仓库列表");

        // 获取仓库详情
        repoGroup.MapGet("/{id}", async (
            string id,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.GetRepositoryByIdAsync(id);
            if (result == null)
                return Results.NotFound(new { success = false, message = "仓库不存在" });
            return Results.Ok(new { success = true, data = result });
        })
        .WithName("AdminGetRepository")
        .WithSummary("获取仓库详情");

        // 获取仓库深度管理信息（分支、语言、增量任务）
        repoGroup.MapGet("/{id}/management", async (
            string id,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.GetRepositoryManagementAsync(id);
            if (result == null)
                return Results.NotFound(new { success = false, message = "仓库不存在" });
            return Results.Ok(new { success = true, data = result });
        })
        .WithName("AdminGetRepositoryManagement")
        .WithSummary("获取仓库深度管理信息");

        // 更新仓库
        repoGroup.MapPut("/{id}", async (
            string id,
            [FromBody] UpdateRepositoryRequest request,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.UpdateRepositoryAsync(id, request);
            if (!result)
                return Results.NotFound(new { success = false, message = "仓库不存在" });
            return Results.Ok(new { success = true, message = "更新成功" });
        })
        .WithName("AdminUpdateRepository")
        .WithSummary("更新仓库");

        // 删除仓库
        repoGroup.MapDelete("/{id}", async (
            string id,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.DeleteRepositoryAsync(id);
            if (!result)
                return Results.NotFound(new { success = false, message = "仓库不存在" });
            return Results.Ok(new { success = true, message = "删除成功" });
        })
        .WithName("AdminDeleteRepository")
        .WithSummary("删除仓库");

        // 更新仓库状态
        repoGroup.MapPut("/{id}/status", async (
            string id,
            [FromBody] UpdateStatusRequest request,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.UpdateRepositoryStatusAsync(id, request.Status);
            if (!result)
                return Results.NotFound(new { success = false, message = "仓库不存在" });
            return Results.Ok(new { success = true, message = "状态更新成功" });
        })
        .WithName("AdminUpdateRepositoryStatus")
        .WithSummary("更新仓库状态");

        // 同步单个仓库统计信息
        repoGroup.MapPost("/{id}/sync-stats", async (
            string id,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.SyncRepositoryStatsAsync(id);
            return Results.Ok(new { success = result.Success, message = result.Message, data = result });
        })
        .WithName("AdminSyncRepositoryStats")
        .WithSummary("同步仓库统计信息");

        // 获取仓库扫描策略
        repoGroup.MapGet("/{id}/scan-plan", async (
            string id,
            string? branchId,
            [FromServices] IAdminRepositoryService repositoryService,
            [FromServices] OpenDeepWiki.EFCore.IContext context,
            CancellationToken cancellationToken) =>
        {
            var result = await repositoryService.GetScanPlanAsync(id);
            if (result == null)
                return Results.NotFound(new { success = false, message = "仓库不存在" });
            var branch = await context.RepositoryBranches.AsNoTracking()
                .Where(item => item.RepositoryId == id && !item.IsDeleted && (branchId == null || item.Id == branchId))
                .OrderByDescending(item => item.LastProcessedAt).ThenBy(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            var plan = DeepRepositoryScan.ReadPlan(branch?.DeepScanPlanJson);
            result.DeepPlan = plan;
            if (branch is not null)
            {
                result.LatestEvaluation = await context.BranchGenerationTasks.AsNoTracking()
                    .Where(task => task.BranchId == branch.Id && !task.IsDeleted &&
                        task.Mode == BranchGenerationTaskMode.ScanPlan)
                    .OrderByDescending(task => task.Status == BranchGenerationTaskStatus.Pending ||
                        task.Status == BranchGenerationTaskStatus.Processing)
                    .ThenByDescending(task => task.UpdatedAt ?? task.CreatedAt).ThenByDescending(task => task.Id)
                    .Select(task => new ScanPlanEvaluationDto(task.Id, task.Status.ToString(), task.ErrorMessage))
                    .FirstOrDefaultAsync(cancellationToken);
            }
            if (plan is not null && branch is not null)
            {
                var entries = await context.BranchLanguages.AsNoTracking()
                    .Where(item => item.RepositoryBranchId == branch.Id && !item.IsDeleted)
                    .Select(item => new { item.LanguageCode, item.DeepScanProgressJson }).ToListAsync(cancellationToken);
                result.DeepProgress = entries.Select(item => new DeepScanLanguageProgress(item.LanguageCode,
                    DeepRepositoryScan.ReadProgress(item.DeepScanProgressJson))).ToArray();
            }
            return Results.Ok(new { success = true, data = result });
        })
        .WithName("AdminGetRepositoryScanPlan")
        .WithSummary("获取仓库扫描策略");

        // 更新仓库扫描策略
        repoGroup.MapPut("/{id}/scan-plan", async (
            string id,
            [FromBody] UpdateRepositoryScanPlanRequest request,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.UpdateScanPlanAsync(id, request);
            if (result == null)
                return Results.NotFound(new { success = false, message = "仓库不存在" });
            return Results.Ok(new { success = true, data = result });
        })
        .WithName("AdminUpdateRepositoryScanPlan")
        .WithSummary("更新仓库扫描策略");

        // 重新评估仓库扫描策略
        repoGroup.MapPost("/{id}/scan-plan/reevaluate", async (
            string id,
            string? branchId,
            [FromServices] OpenDeepWiki.EFCore.IContext context,
            [FromServices] IBranchGenerationTaskService taskService,
            CancellationToken cancellationToken) =>
        {
            var branch = await context.RepositoryBranches.AsNoTracking()
                .Where(item => item.RepositoryId == id && !item.IsDeleted && (branchId == null || item.Id == branchId))
                .OrderByDescending(item => item.LastProcessedAt).ThenBy(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (branch is null)
                return Results.NotFound(new { success = false, message = "仓库或分支不存在" });
            var result = await taskService.EnqueueScanPlanAsync(id, branch.Id, cancellationToken);
            return result.Success
                ? Results.Accepted(value: new { success = true, taskId = result.Task!.Id })
                : Results.Conflict(new { success = false, message = result.ErrorMessage });
        })
        .WithName("AdminReevaluateRepositoryScanPlan")
        .WithSummary("重新评估仓库扫描策略");

        // 触发仓库全量重生成
        repoGroup.MapPost("/{id}/regenerate", async (
            string id,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.RegenerateRepositoryAsync(id);
            var response = new { success = result.Success, message = result.Message, data = result };
            return result.Success
                ? Results.Ok(response)
                : result.StatusCode == StatusCodes.Status409Conflict
                    ? Results.Conflict(response)
                    : Results.BadRequest(response);
        })
        .WithName("AdminRegenerateRepository")
        .WithSummary("触发仓库全量重生成");

        // 获取 Graphify 生成状态
        repoGroup.MapGet("/{id}/graphify", async (
            string id,
            [FromServices] IGraphifyArtifactService graphifyService,
            CancellationToken cancellationToken) =>
        {
            var result = await graphifyService.GetRepositoryArtifactsAsync(id, cancellationToken);
            return Results.Ok(new { success = true, data = result });
        })
        .WithName("AdminGetRepositoryGraphifyArtifacts")
        .WithSummary("获取仓库 Graphify 生成状态");

        // 触发 Graphify 生成
        repoGroup.MapPost("/{id}/graphify/generate", async (
            string id,
            [FromBody] GenerateGraphifyArtifactRequest request,
            [FromServices] IGraphifyArtifactService graphifyService,
            CancellationToken cancellationToken) =>
        {
            var result = await graphifyService.EnqueueGenerationAsync(id, request.BranchId, cancellationToken);
            return Results.Ok(new { success = result.Success, message = result.Message, data = result });
        })
        .WithName("AdminGenerateRepositoryGraphify")
        .WithSummary("触发仓库 Graphify 图谱生成");

        // 触发指定文档重生成
        repoGroup.MapPost("/{id}/documents/regenerate", async (
            string id,
            [FromBody] RegenerateRepositoryDocumentRequest request,
            [FromServices] IAdminRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            var result = await repositoryService.RegenerateDocumentAsync(id, request, cancellationToken);
            return Results.Ok(new { success = result.Success, message = result.Message, data = result });
        })
        .WithName("AdminRegenerateRepositoryDocument")
        .WithSummary("触发指定文档重生成");

        // 批量触发仓库全量重生成
        repoGroup.MapPost("/batch/regenerate", async (
            [FromBody] BatchOperationRequest request,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.BatchRegenerateRepositoriesAsync(request.Ids);
            return Results.Ok(new { success = true, data = result });
        })
        .WithName("AdminBatchRegenerateRepositories")
        .WithSummary("批量触发仓库全量重生成");

        // 手动更新指定文档内容
        repoGroup.MapPut("/{id}/documents/content", async (
            string id,
            [FromBody] UpdateRepositoryDocumentContentRequest request,
            [FromServices] IAdminRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            var result = await repositoryService.UpdateDocumentContentAsync(id, request, cancellationToken);
            return Results.Ok(new { success = result.Success, message = result.Message, data = result });
        })
        .WithName("AdminUpdateRepositoryDocumentContent")
        .WithSummary("手动更新指定文档内容");

        // 批量同步仓库统计信息
        repoGroup.MapPost("/batch/sync-stats", async (
            [FromBody] BatchOperationRequest request,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.BatchSyncRepositoryStatsAsync(request.Ids);
            return Results.Ok(new { success = true, data = result });
        })
        .WithName("AdminBatchSyncRepositoryStats")
        .WithSummary("批量同步仓库统计信息");

        // 批量删除仓库
        repoGroup.MapPost("/batch/delete", async (
            [FromBody] BatchOperationRequest request,
            [FromServices] IAdminRepositoryService repositoryService) =>
        {
            var result = await repositoryService.BatchDeleteRepositoriesAsync(request.Ids);
            return Results.Ok(new { success = true, data = result });
        })
        .WithName("AdminBatchDeleteRepositories")
        .WithSummary("批量删除仓库");

        return group;
    }
}
