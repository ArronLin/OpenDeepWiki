using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories;

public sealed class LocalDirectoryAccess(IContext context, IOptions<RepositoryAnalyzerOptions> options)
{
    private const string SettingKey = "repository.localImport.allowedRoots";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private string[] DeploymentRoots => options.Value.AllowedLocalPathRoots
        .Where(root => !string.IsNullOrWhiteSpace(root))
        .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim())))
        .Distinct(PathComparer).ToArray();

    public async Task<string[]> GetRootsAsync()
    {
        var ceiling = DeploymentRoots;
        var setting = await FindSettingAsync();
        if (setting is null)
            return ceiling;

        return (JsonSerializer.Deserialize<string[]>(setting.Value ?? "[]") ?? [])
            .Where(path => ceiling.Any(root => Contains(root, path))).Distinct(PathComparer).ToArray();
    }

    public async Task<string[]> ReplaceRootsAsync(string[] roots)
    {
        if (roots.Length > 50)
            throw new ArgumentException("最多可以配置 50 个允许目录");

        var ceiling = DeploymentRoots;
        var normalized = roots.Select(path => Validate(path, ceiling)).Distinct(PathComparer).ToArray();
        var setting = await FindSettingAsync();
        if (setting is null)
        {
            setting = new SystemSetting { Id = Guid.NewGuid().ToString(), Key = SettingKey, Category = "security" };
            context.SystemSettings.Add(setting);
        }
        setting.Value = JsonSerializer.Serialize(normalized);
        setting.UpdateTimestamp();
        await context.SaveChangesAsync();
        return normalized;
    }

    public async Task<string> ResolveAllowedAsync(string path) => Validate(path, await GetRootsAsync());

    public async Task<IReadOnlyList<DirectoryBreadcrumb>> GetBreadcrumbsAsync(string current)
    {
        var roots = await GetRootsAsync();
        var result = new List<DirectoryBreadcrumb>();
        for (DirectoryInfo? directory = new(current); directory is not null &&
             roots.Any(root => Contains(root, directory.FullName)); directory = directory.Parent)
        {
            result.Add(new DirectoryBreadcrumb(directory.Name, DisplayPath(directory.FullName)));
        }
        result.Reverse();
        if (result.Count > 0)
            result[0] = result[0] with { Name = result[0].Path };
        return result;
    }

    public sealed record DirectoryBreadcrumb(string Name, string Path);

    public string DisplayPath(string path)
    {
        foreach (var mapping in options.Value.LocalPathMappings
                     .Where(item => !string.IsNullOrWhiteSpace(item.HostPath) && Path.IsPathFullyQualified(item.ContainerPath))
                     .OrderByDescending(item => item.ContainerPath.Length))
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mapping.ContainerPath));
            if (!Contains(target, path))
                continue;

            var relative = Path.GetRelativePath(target, path);
            var separator = mapping.HostPath.Contains('\\') ? '\\' : '/';
            var host = relative == "." ? mapping.HostPath : mapping.HostPath.TrimEnd('/', '\\') +
                separator + relative.Replace(Path.DirectorySeparatorChar, separator);
            try
            {
                if (LocalDirectoryPathResolver.Resolve(host, options.Value.LocalPathMappings).Equals(path, PathComparison))
                    return host;
            }
            catch (ArgumentException)
            {
                // A native filename can contain characters unsupported by the mapped host path.
            }
        }
        return path;
    }

    private Task<SystemSetting?> FindSettingAsync() =>
        context.SystemSettings.SingleOrDefaultAsync(setting => setting.Key == SettingKey);

    private string Validate(string path, string[] roots)
    {
        var full = Path.TrimEndingDirectorySeparator(LocalDirectoryPathResolver.Resolve(path, options.Value.LocalPathMappings));
        if (!roots.Any(root => Contains(root, full)))
            throw new InvalidOperationException("本地目录不在允许导入的白名单范围内");
        var segments = new Stack<DirectoryInfo>();
        for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
            segments.Push(directory);
        foreach (var directory in segments)
        {
            var attributes = directory.Attributes;
            if (attributes != (FileAttributes)(-1) && attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("请选择实际目录，不能通过符号链接或目录联接访问");
            if (attributes == (FileAttributes)(-1) || !attributes.HasFlag(FileAttributes.Directory))
                throw new DirectoryNotFoundException("找不到该目录。请确认本机目录存在，且已挂载到后端容器并配置正确的路径映射。");
        }
        return full;
    }

    private static bool Contains(string root, string path) => path.Equals(root, PathComparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);
}
