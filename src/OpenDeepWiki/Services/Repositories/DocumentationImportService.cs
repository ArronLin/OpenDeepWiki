using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.Cache.Abstractions;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models;
using OpenDeepWiki.Services.Auth;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>Imports documents directly into the read model; never schedules generation or accesses source.</summary>
public sealed class DocumentationImportService(IContext context, IUserContext userContext, ICache cache)
{
    public const long MaxUploadBytes = 50 * 1024 * 1024;
    private const long MaxExpandedBytes = 100 * 1024 * 1024;
    private const int MaxEntries = 5000;
    private const int MaxDepth = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Repository> ImportAsync(IFormFile file, string orgName, string repoName,
        bool isPublic = false, string? branchName = null, string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        var userId = userContext.UserId;
        if (string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("用户未登录");

        orgName = ValidateName(orgName, 100, "组织名", allowSlash: true);
        repoName = ValidateName(repoName, 100, "知识库名称");
        if (file.Length <= 0 || file.Length > MaxUploadBytes ||
            !file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请上传不超过 50 MB 的文档 ZIP");
        if (file.Length < 22)
            throw new InvalidDataException("ZIP 文件损坏或不完整");

        // Check before reading and never merge/delete/overwrite any existing repository, even soft-deleted ones.
        if (await context.Repositories.AnyAsync(r => r.OrgName.ToLower() == orgName.ToLower() &&
                r.RepoName.ToLower() == repoName.ToLower(), cancellationToken))
            throw new InvalidOperationException("该组织下已有同名知识库，请更换名称；已有内容不会被覆盖");

        await using var input = file.OpenReadStream();
        using var archive = OpenArchive(input);
        var entries = ValidateEntries(archive);
        var manifest = entries.Values.SingleOrDefault(e => e.FullName == DocumentationArchive.EntryName);
        DocumentationArchive snapshot;
        if (manifest is not null)
        {
            try
            {
                snapshot = JsonSerializer.Deserialize<DocumentationArchive>(
                    await ReadTextAsync(manifest, cancellationToken), JsonOptions)
                    ?? throw new InvalidDataException("文档元数据为空");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("文档元数据格式无效", ex);
            }
        }
        else
        {
            snapshot = await ReadLegacyAsync(entries, cancellationToken);
        }

        ValidateSnapshot(snapshot);
        branchName = ValidateName(string.IsNullOrWhiteSpace(branchName) ? snapshot.BranchName : branchName,
            200, "分支名", allowSlash: true);
        languageCode = ValidateName(string.IsNullOrWhiteSpace(languageCode) ? snapshot.LanguageCode : languageCode,
            50, "语言代码");

        var now = DateTime.UtcNow;
        var repository = new Repository
        {
            Id = Guid.NewGuid().ToString(), OwnerUserId = userId, OrgName = orgName, RepoName = repoName,
            GitUrl = RepositorySource.DocumentationPrefix + Guid.NewGuid().ToString("N"),
            IsPublic = isPublic, Status = RepositoryStatus.Completed, GenerateSkill = true
        };
        var branch = new RepositoryBranch
        {
            Id = Guid.NewGuid().ToString(), RepositoryId = repository.Id, BranchName = branchName,
            LastProcessedAt = now, GenerationStatus = BranchGenerationTaskStatus.Completed,
            LastGenerationCompletedAt = now
        };
        var language = new BranchLanguage
        {
            Id = Guid.NewGuid().ToString(), RepositoryBranchId = branch.Id, LanguageCode = languageCode,
            IsDefault = true, MindMapContent = snapshot.MindMapContent,
            MindMapStatus = string.IsNullOrWhiteSpace(snapshot.MindMapContent)
                ? MindMapStatus.Failed : MindMapStatus.Completed
        };
        var ids = snapshot.Catalogs.ToDictionary(c => c.Id, _ => Guid.NewGuid().ToString());
        var catalogs = snapshot.Catalogs.Select(c => new DocCatalog
        {
            Id = ids[c.Id], BranchLanguageId = language.Id,
            ParentId = c.ParentId is null ? null : ids[c.ParentId], Title = c.Title, Path = c.Path, Order = c.Order,
            DocFile = c.Content is null ? null : new DocFile
            {
                Id = Guid.NewGuid().ToString(), BranchLanguageId = language.Id,
                Content = c.Content, SourceFiles = c.SourceFiles
            }
        }).ToList();
        foreach (var catalog in catalogs)
            catalog.DocFileId = catalog.DocFile?.Id;

        // Rebuild the descriptor deterministically for the local name, without an AI dependency.
        language.SkillMarkdown = new RepositorySkillMarkdownBuilder()
            .BuildSkillMarkdown(repository, branch, language, catalogs, now);
        language.SkillGeneratedAt = now;
        context.Repositories.Add(repository);
        context.RepositoryBranches.Add(branch);
        context.BranchLanguages.Add(language);
        context.DocCatalogs.AddRange(catalogs);
        // One SaveChanges: EF's transaction commits the complete graph or nothing.
        await context.SaveChangesAsync(cancellationToken);
        await cache.SetAsync(RepositoryPublicReadCache.BuildVersionKey(orgName, repoName),
            Guid.NewGuid().ToString("N"), new CacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = RepositoryPublicReadCache.VersionTtl
            }, cancellationToken);
        return repository;
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("ZIP 条目过多（最多 5000 个）");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName;
            ValidatePath(path.TrimEnd('/'));
            expanded += entry.Length;
            if (entry.Length > MaxUploadBytes || expanded > MaxExpandedBytes)
                throw new InvalidDataException("ZIP 解压内容过大（单项最多 50 MB，总计最多 100 MB）");
            if (!entries.TryAdd(path, entry))
                throw new InvalidDataException("ZIP 包含重复路径");
        }
        return entries;
    }

    private static ZipArchive OpenArchive(Stream input)
    {
        try { return new ZipArchive(input, ZipArchiveMode.Read); }
        catch (ArgumentOutOfRangeException ex)
        {
            // FormFile's bounded stream rejects negative seeks made while scanning a broken ZIP footer.
            throw new InvalidDataException("ZIP 文件损坏或不完整", ex);
        }
    }

    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var bounded = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (bounded.Length + count > entry.Length || bounded.Length + count > MaxUploadBytes)
                throw new InvalidDataException("ZIP 条目实际大小超过声明值或导入限制");
            bounded.Write(buffer, 0, count);
        }
        if (bounded.Length != entry.Length)
            throw new InvalidDataException("ZIP 条目内容不完整");
        bounded.Position = 0;
        using var reader = new StreamReader(bounded, new UTF8Encoding(false, true), true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<DocumentationArchive> ReadLegacyAsync(
        Dictionary<string, ZipArchiveEntry> entries, CancellationToken cancellationToken)
    {
        var snapshot = new DocumentationArchive();
        var skill = entries.GetValueOrDefault("SKILL.md");
        if (skill is not null)
        {
            // The exporter writes these JSON-compatible quoted YAML scalars. Never execute SKILL content.
            var lines = (await ReadTextAsync(skill, cancellationToken)).Split('\n');
            snapshot.BranchName = ReadSkillMetadata(lines, "branch") ?? "main";
            snapshot.LanguageCode = ReadSkillMetadata(lines, "language") ?? "en";
        }
        var hasDocsRoot = entries.Keys.Any(p => p.StartsWith("references/docs/", StringComparison.OrdinalIgnoreCase));
        var nodes = new Dictionary<string, DocumentationArchiveCatalog>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Values.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (!entry.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
            if (hasDocsRoot && !entry.FullName.StartsWith("references/docs/", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = hasDocsRoot ? entry.FullName["references/docs/".Length..] : entry.FullName;
            var parts = relative[..^3].Split('/');
            string? parentId = null;
            for (var i = 0; i < parts.Length; i++)
            {
                var path = string.Join('/', parts.Take(i + 1));
                if (!nodes.TryGetValue(path, out var node))
                {
                    node = new DocumentationArchiveCatalog
                    {
                        Id = Guid.NewGuid().ToString(), ParentId = parentId, Path = path,
                        Title = parts[i], Order = nodes.Count
                    };
                    nodes.Add(path, node);
                }
                parentId = node.Id;
                if (i == parts.Length - 1)
                    node.Content = await ReadTextAsync(entry, cancellationToken);
            }
        }
        snapshot.Catalogs = nodes.Values.ToList();
        return snapshot;
    }

    private static string? ReadSkillMetadata(IEnumerable<string> lines, string key)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith($"  {key}: ", StringComparison.Ordinal));
        if (line is null) return null;
        var value = line[(key.Length + 4)..].Trim();
        try { return value.StartsWith('"') ? JsonSerializer.Deserialize<string>(value) : value; }
        catch (JsonException) { throw new InvalidDataException("SKILL.md 元数据无效"); }
    }

    private static void ValidateSnapshot(DocumentationArchive snapshot)
    {
        if (snapshot.Format != "opendeepwiki-documents" || snapshot.Version != 1)
            throw new InvalidDataException("不支持的文档包格式或版本");
        if (snapshot.Catalogs is null || snapshot.Catalogs.Count == 0 || snapshot.Catalogs.Count > MaxEntries ||
            !snapshot.Catalogs.Any(c => c is not null && c.Content is not null))
            throw new InvalidDataException("ZIP 中没有可导入的 Markdown 文档或目录条目过多");
        var ids = new Dictionary<string, DocumentationArchiveCatalog>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot.Catalogs)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || !ids.TryAdd(node.Id, node))
                throw new InvalidDataException("文档目录 ID 为空或重复");
            if (string.IsNullOrWhiteSpace(node.Title) || node.Title.Length > 500)
                throw new InvalidDataException("文档标题为空或过长");
            ValidatePath(node.Path);
            if (!paths.Add(node.Path)) throw new InvalidDataException("文档路径重复");
            if (node.SourceFiles is not null)
            {
                try
                {
                    var sourceFiles = JsonSerializer.Deserialize<List<string>>(node.SourceFiles);
                    if (sourceFiles is null || sourceFiles.Any(s => s is null))
                        throw new InvalidDataException("文档来源文件列表无效");
                }
                catch (JsonException ex) { throw new InvalidDataException("文档来源文件列表无效", ex); }
            }
        }
        foreach (var node in snapshot.Catalogs)
        {
            var visited = new HashSet<string> { node.Id };
            var parentId = node.ParentId;
            while (parentId is not null)
            {
                if (!ids.TryGetValue(parentId, out var parent) || !visited.Add(parentId) || visited.Count > MaxDepth)
                    throw new InvalidDataException("文档目录包含缺失父节点、循环或层级过深");
                parentId = parent.ParentId;
            }
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1000 || path.Contains('\\') ||
            path.Any(char.IsControl) || path.Split('/').Length > MaxDepth ||
            path.Split('/').Any(s => s is "" or "." or ".." || s.Contains(':')))
            throw new InvalidDataException("文档包包含无效或不安全的路径");
    }

    private static string ValidateName(string? value, int maxLength, string label, bool allowSlash = false)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength ||
            value.Any(c => char.IsControl(c) || "\\?#%:".Contains(c)) ||
            (!allowSlash && value.Contains('/')) || value.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException($"{label}为空、过长或包含无效字符");
        return value;
    }
}
