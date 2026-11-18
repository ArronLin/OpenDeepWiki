namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Translates configured host roots into backend paths without depending on the host OS.
/// Mappings describe existing mounts; they never create mounts or expand the import allowlist.
/// </summary>
public static class LocalDirectoryPathResolver
{
    public static string Resolve(string path, IReadOnlyCollection<LocalPathMapping> mappings)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("请输入本机目录的绝对路径");

        var input = NormalizeHostPath(path.Trim());
        var comparison = IsWindowsPath(input) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var mapping = mappings
            .Where(item => !string.IsNullOrWhiteSpace(item.HostPath) && !string.IsNullOrWhiteSpace(item.ContainerPath))
            .Select(item => new { Root = NormalizeHostPath(item.HostPath.Trim()), item.ContainerPath })
            .Where(item => IsWithinRoot(input, item.Root, comparison))
            .OrderByDescending(item => item.Root.Length)
            .FirstOrDefault();

        if (mapping is not null)
        {
            if (!Path.IsPathFullyQualified(mapping.ContainerPath))
                throw new InvalidOperationException("本机路径映射的目标必须是后端服务器上的绝对路径，请检查配置");

            var relativePath = input[mapping.Root.Length..].TrimStart('/');
            return Path.GetFullPath(Path.Combine(mapping.ContainerPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        if (!OperatingSystem.IsWindows() && IsWindowsPath(input))
            throw new ArgumentException("该本机路径尚未配置到容器的目录映射。请为所在目录配置挂载和 HostPath/ContainerPath 映射后重试。");

        if (!Path.IsPathFullyQualified(input))
            throw new ArgumentException("请输入本机目录的绝对路径");

        return Path.GetFullPath(input);
    }

    private static bool IsWithinRoot(string path, string root, StringComparison comparison)
    {
        return path.Equals(root, comparison) ||
               path.StartsWith(root.TrimEnd('/') + "/", comparison);
    }

    private static bool IsWindowsPath(string path) =>
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static string NormalizeHostPath(string path)
    {
        // Windows separators must be normalized before Path.GetFullPath: on Linux they are
        // ordinary filename characters, and a drive path would otherwise become /app/D:\...
        var normalized = path.Replace('\\', '/');
        var isWindows = IsWindowsPath(normalized);
        string root;
        string remainder;
        if (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) &&
            normalized[1] == ':' && normalized[2] == '/')
        {
            root = normalized[..3];
            remainder = normalized[3..];
        }
        else if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            var parts = normalized[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Take(2).Any(part => part is "." or ".." || HasInvalidWindowsCharacters(part)))
                throw new ArgumentException("请输入有效的 UNC 共享目录路径");
            root = "//" + parts[0] + "/" + parts[1];
            remainder = string.Join('/', parts.Skip(2));
        }
        else if (normalized.StartsWith('/') && !isWindows)
        {
            root = "/";
            remainder = normalized[1..];
        }
        else
        {
            throw new ArgumentException("请输入本机目录的绝对路径，例如 D:\\repos\\OpenDeepWiki");
        }

        var segments = new List<string>();
        foreach (var segment in remainder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new ArgumentException("目录路径不能越过文件系统根目录");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (segment.Contains('\0') || (isWindows && HasInvalidWindowsCharacters(segment)))
                throw new ArgumentException("目录路径包含无效字符");
            segments.Add(segment);
        }

        return segments.Count == 0 ? root : root.TrimEnd('/') + "/" + string.Join('/', segments);
    }

    private static bool HasInvalidWindowsCharacters(string segment) =>
        segment.Any(character => character < ' ' || "<>:\"|?*".Contains(character));
}

public class LocalPathMapping
{
    public string HostPath { get; set; } = string.Empty;
    public string ContainerPath { get; set; } = string.Empty;
}
