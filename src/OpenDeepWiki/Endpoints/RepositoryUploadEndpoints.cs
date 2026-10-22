using Microsoft.AspNetCore.Mvc;
using OpenDeepWiki.Models;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// Hand-written multipart endpoints for repository uploads.
/// The MiniApi source generator does not preserve the [FromForm] binding, so the generated
/// /submit-archive endpoint is registered as a JSON body and rejects multipart/form-data with 415.
/// This endpoint reads the multipart form explicitly and delegates to the existing service logic.
/// </summary>
public static class RepositoryUploadEndpoints
{
    public static IEndpointRouteBuilder MapRepositoryUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/repositories/submit-archive", SubmitArchiveAsync)
            .WithTags("仓库")
            .WithName("SubmitArchiveRepository")
            .WithSummary("通过 ZIP 压缩包创建仓库")
            .DisableAntiforgery();

        app.MapPost("/api/v1/repositories/import-documents", ImportDocumentsAsync)
            .WithTags("仓库")
            .WithName("ImportRepositoryDocuments")
            .WithSummary("导入已生成的文档 ZIP（无需源码或模型）")
            .RequireAuthorization()
            .WithMetadata(new RequestSizeLimitAttribute(DocumentationImportService.MaxUploadBytes + 1024 * 1024))
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> ImportDocumentsAsync(HttpRequest request,
        [FromServices] DocumentationImportService service, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new { message = "请使用 multipart/form-data 格式上传" });
        try
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files["archive"];
            if (file is null)
                return Results.BadRequest(new { message = "请上传文档 ZIP" });
            return Results.Ok(await service.ImportAsync(file,
                form["orgName"].ToString(), form["repoName"].ToString(),
                bool.TryParse(form["isPublic"], out var isPublic) && isPublic,
                form["branchName"].ToString(), form["languageCode"].ToString(), cancellationToken));
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (System.Text.DecoderFallbackException)
        {
            return Results.BadRequest(new { message = "文档包必须使用有效的 UTF-8 文本编码" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
    }

    private static async Task<IResult> SubmitArchiveAsync(
        HttpRequest httpRequest,
        [FromServices] RepositoryService repositoryService)
    {
        if (!httpRequest.HasFormContentType)
        {
            return Results.BadRequest(new { message = "请使用 multipart/form-data 格式上传" });
        }

        var form = await httpRequest.ReadFormAsync();
        var archive = form.Files["archive"];

        var branchName = form["branchName"].ToString();
        var request = new ArchiveRepositorySubmitRequest
        {
            OrgName = form["orgName"].ToString(),
            RepoName = form["repoName"].ToString(),
            BranchName = string.IsNullOrWhiteSpace(branchName) ? "main" : branchName,
            LanguageCode = form["languageCode"].ToString(),
            IsPublic = bool.TryParse(form["isPublic"], out var isPublic) && isPublic,
            GenerateSkill = bool.TryParse(form["generateSkill"], out var generateSkill) && generateSkill,
            Archive = archive,
        };

        var repository = await repositoryService.SubmitArchiveAsync(request);
        return Results.Ok(repository);
    }
}
