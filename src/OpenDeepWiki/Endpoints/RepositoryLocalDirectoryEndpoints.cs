using Microsoft.AspNetCore.Mvc;
using OpenDeepWiki.Models;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Endpoints;

public static class RepositoryLocalDirectoryEndpoints
{
    public static IEndpointRouteBuilder MapRepositoryLocalDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        var directories = app.MapGroup("/api/v1/repositories/local-directories")
            .RequireAuthorization().WithTags("仓库");
        directories.MapGet("/roots", async (LocalDirectoryAccess access, HttpContext http) =>
            Results.Ok(new
            {
                roots = (await access.GetRootsAsync()).Select(access.DisplayPath),
                canEdit = http.User.IsInRole("Admin")
            }));
        directories.MapPut("/roots", ReplaceRootsAsync).RequireAuthorization("AdminOnly");
        directories.MapGet("/children", BrowseAsync);

        app.MapPost("/api/v1/repositories/submit-local", SubmitAsync)
            .WithTags("仓库")
            .WithName("SubmitLocalDirectoryRepository")
            .WithSummary("通过服务器本地目录创建仓库")
            .RequireAuthorization();
        return app;
    }

    private static async Task<IResult> ReplaceRootsAsync(string[] roots, LocalDirectoryAccess access)
    {
        try
        {
            return Results.Ok(new { roots = (await access.ReplaceRootsAsync(roots)).Select(access.DisplayPath), canEdit = true });
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.BadRequest(new { message = "后端没有权限访问该目录" });
        }
    }

    private static async Task<IResult> BrowseAsync(string path, string? prefix, string? search, int? offset,
        int? pageSize, LocalDirectoryAccess access)
    {
        try
        {
            var current = await access.ResolveAllowedAsync(path);
            var limit = Math.Clamp(pageSize ?? 100, 1, 200);
            var start = Math.Max(0, offset ?? 0);
            var folders = new DirectoryInfo(current).EnumerateDirectories("*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                })
                .Where(folder => !folder.Name.StartsWith('.') && (search is not null
                    ? folder.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                    : string.IsNullOrWhiteSpace(prefix) || folder.Name.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(folder => folder.Name, StringComparer.Ordinal)
                .Skip(start)
                .Take(limit + 1)
                .ToArray();
            var breadcrumbs = await access.GetBreadcrumbsAsync(current);
            return Results.Ok(new
            {
                path = access.DisplayPath(current),
                folders = folders.Take(limit).Select(folder => new { folder.Name, path = access.DisplayPath(folder.FullName) }).ToArray(),
                truncated = folders.Length > limit,
                nextOffset = folders.Length > limit ? (int?)(start + limit) : null,
                parentPath = breadcrumbs.Count > 1 ? breadcrumbs[^2].Path : null,
                breadcrumbs
            });
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (IOException)
        {
            return Results.BadRequest(new { message = "无法读取该目录，请检查挂载和目录权限" });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.BadRequest(new { message = "后端没有权限访问该目录" });
        }
    }

    public static async Task<IResult> SubmitAsync(
        [FromBody] LocalDirectoryRepositorySubmitRequest request,
        [FromServices] RepositoryService service)
    {
        try
        {
            return Results.Ok(await service.SubmitLocalDirectoryAsync(request));
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.BadRequest(new { message = "后端没有权限访问该目录" });
        }
    }
}
