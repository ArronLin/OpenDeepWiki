using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Moq;
using OpenDeepWiki.Cache.Abstractions;
using OpenDeepWiki.Cache.Memory;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.MCP;
using OpenDeepWiki.Models;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Graphify;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public sealed class DocumentationImportTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestContext context;
    private readonly ICache cache = new MemoryCacheAdapter(new MemoryCache(new MemoryCacheOptions()));
    private readonly Mock<IUserContext> user = new();

    public DocumentationImportTests()
    {
        connection.Open();
        context = new TestContext(new DbContextOptionsBuilder<TestContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        context.Users.Add(new User { Id = "local-user", Name = "Local", Email = "local@example.test" });
        context.SaveChanges();
        user.SetupGet(u => u.UserId).Returns("local-user");
    }

    [Fact]
    public async Task ExportAndImportAcrossInstances_PreservesPathsContentAndExistingKnowledgeBase()
    {
        // The destination contains an unrelated repository before the other machine's snapshot arrives.
        var importer = new DocumentationImportService(context, user.Object, cache);
        await importer.ImportAsync(Zip(("old.md", "Existing local content")), "local", "existing");

        using var remoteContext = new TestContext(new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var source = Snapshot();
        var remoteRepository = new Repository
        {
            Id = "remote-repo", OrgName = "remote", RepoName = "project", OwnerUserId = "remote-owner",
            GitUrl = "https://example.test/private.git", AuthPassword = "never-export-this",
            Status = RepositoryStatus.Completed, GenerateSkill = true
        };
        var branch = new RepositoryBranch { Id = "remote-branch", RepositoryId = remoteRepository.Id, BranchName = source.BranchName };
        var language = new BranchLanguage
        {
            Id = "remote-lang", RepositoryBranchId = branch.Id, LanguageCode = source.LanguageCode,
            IsDefault = true, MindMapContent = source.MindMapContent
        };
        remoteContext.AddRange(remoteRepository, branch, language);
        remoteContext.DocCatalogs.AddRange(source.Catalogs.Select(c => new DocCatalog
        {
            Id = c.Id, ParentId = c.ParentId, Title = c.Title, Path = c.Path, Order = c.Order,
            BranchLanguageId = language.Id,
            DocFile = c.Content is null ? null : new DocFile
            {
                Id = Guid.NewGuid().ToString(), BranchLanguageId = language.Id, Content = c.Content,
                SourceFiles = c.SourceFiles
            }
        }));
        await remoteContext.SaveChangesAsync();
        var export = Assert.IsType<FileContentResult>(await Docs(remoteContext).ExportAsync("remote", "project"));
        using var exported = new ZipArchive(new MemoryStream(export.FileContents));
        var manifest = exported.GetEntry(DocumentationArchive.EntryName);
        Assert.NotNull(manifest);
        using (var reader = new StreamReader(manifest.Open()))
            Assert.DoesNotContain("never-export-this", await reader.ReadToEndAsync());

        var imported = await importer.ImportAsync(File(export.FileContents), "local/team", "imported", true);
        Assert.Equal(RepositoryStatus.Completed, imported.Status);
        Assert.Equal(RepositorySourceType.Documentation, imported.SourceType);
        Assert.Null(imported.AuthPassword);
        Assert.Equal("local-user", imported.OwnerUserId);
        Assert.Equal(2, await context.Repositories.CountAsync());
        Assert.Equal("Existing local content", (await context.DocFiles.FirstAsync(d => d.Content == "Existing local content")).Content);
        var importedBranch = await context.RepositoryBranches.SingleAsync(b => b.RepositoryId == imported.Id);
        Assert.Equal("release/docs", importedBranch.BranchName);
        var importedLanguage = await context.BranchLanguages.SingleAsync(l => l.RepositoryBranchId == importedBranch.Id);
        Assert.Equal("zh", importedLanguage.LanguageCode);
        Assert.Equal(source.MindMapContent, importedLanguage.MindMapContent);
        var importedCatalogs = await context.DocCatalogs.Where(c => c.BranchLanguageId == importedLanguage.Id).ToListAsync();
        Assert.DoesNotContain(importedCatalogs, c => source.Catalogs.Any(s => s.Id == c.Id));
        Assert.Equal(7, importedCatalogs.Single(c => c.Path == "architecture/start").Order);

        // Exercise the same services the local page and MCP use, without a source checkout or AI provider.
        var tree = await Docs(context).GetTreeAsync("local/team", "imported");
        Assert.True(tree.Exists);
        Assert.Equal("architecture/start", tree.DefaultSlug);
        var doc = await Docs(context).GetDocAsync("local/team", "imported", "architecture/start");
        Assert.Equal("# 启动\nCamera sensor starts here.\n", doc.Content);
        Assert.Null(doc.GitUrl);
        Assert.Equal(["src/main.cs"], doc.SourceFiles);
        using var search = JsonDocument.Parse(await McpGlobalTools.SearchDocs(context, "Camera", "local/team", "imported"));
        Assert.True(search.RootElement.GetProperty("count").GetInt32() > 0);
        using var read = JsonDocument.Parse(await McpGlobalTools.ReadDoc(context, "local/team", "imported", "architecture/start"));
        Assert.Equal(doc.Content, read.RootElement.GetProperty("content").GetString());
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
        Assert.Empty(await context.IncrementalUpdateTasks.ToListAsync());
        Assert.Empty(await context.TokenUsages.ToListAsync());
    }

    [Fact]
    public async Task LegacySkillZip_UsesMetadataAndNestedMarkdown()
    {
        var file = Zip(("SKILL.md", "---\nmetadata:\n  branch: \"feature/docs\"\n  language: \"zh\"\n---\n"),
            ("references/docs/目录/入门.md", "旧版文档"), ("references/other.md", "not a wiki page"));
        var repository = await Importer().ImportAsync(file, "team", "legacy");
        var branch = await context.RepositoryBranches.SingleAsync();
        Assert.Equal("feature/docs", branch.BranchName);
        Assert.Equal("zh", (await context.BranchLanguages.SingleAsync()).LanguageCode);
        Assert.Equal("旧版文档", (await context.DocFiles.SingleAsync()).Content);
        Assert.Equal(2, await context.DocCatalogs.CountAsync());
        Assert.Equal(RepositoryStatus.Completed, repository.Status);
    }

    [Fact]
    public async Task SameNameConflict_PreservesExistingDocuments()
    {
        await Importer().ImportAsync(Zip(("readme.md", "original")), "team", "existing");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Importer().ImportAsync(Zip(("readme.md", "replacement")), "TEAM", "EXISTING"));
        Assert.Single(await context.Repositories.ToListAsync());
        Assert.Equal("original", (await context.DocFiles.SingleAsync()).Content);
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/absolute.md")]
    [InlineData("C:/escape.md")]
    [InlineData("docs\\escape.md")]
    public async Task UnsafeZipPaths_AreRejectedWithoutWrites(string path)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Importer().ImportAsync(Zip((path, "unsafe")), "team", "invalid"));
        Assert.Empty(await context.Repositories.ToListAsync());
        Assert.Empty(await context.DocFiles.ToListAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("corrupt")]
    [InlineData("json")]
    [InlineData("version")]
    [InlineData("cycle")]
    [InlineData("missing-parent")]
    [InlineData("duplicate-path")]
    [InlineData("duplicate-id")]
    [InlineData("invalid-source-files")]
    [InlineData("null-catalog")]
    public async Task InvalidPackages_AreRejectedBeforePersistence(string scenario)
    {
        var snapshot = Snapshot();
        switch (scenario)
        {
            case "version": snapshot.Version = 99; break;
            case "cycle": snapshot.Catalogs[0].ParentId = snapshot.Catalogs[1].Id; break;
            case "missing-parent": snapshot.Catalogs[0].ParentId = "absent"; break;
            case "duplicate-path": snapshot.Catalogs[1].Path = snapshot.Catalogs[0].Path; break;
            case "duplicate-id": snapshot.Catalogs[1].Id = snapshot.Catalogs[0].Id; break;
            case "invalid-source-files": snapshot.Catalogs[1].SourceFiles = "{}"; break;
            case "null-catalog": snapshot.Catalogs.Add(null!); break;
        }
        var file = scenario switch
        {
            "empty" => Zip(("code.cs", "source code is not documentation")),
            "corrupt" => File(Encoding.UTF8.GetBytes("not a zip")),
            "json" => Zip((DocumentationArchive.EntryName, "{")),
            _ => Zip((DocumentationArchive.EntryName, JsonSerializer.Serialize(snapshot)))
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => Importer().ImportAsync(file, "team", "invalid"));
        Assert.Empty(await context.Repositories.ToListAsync());
        Assert.Empty(await context.RepositoryBranches.ToListAsync());
        Assert.Empty(await context.DocCatalogs.ToListAsync());
    }

    [Fact]
    public async Task DatabaseFailure_RollsBackEntireImport()
    {
        user.SetupGet(u => u.UserId).Returns("missing-user");
        await Assert.ThrowsAsync<DbUpdateException>(() => Importer().ImportAsync(Zip(("doc.md", "body")), "team", "failed"));
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Repositories.ToListAsync());
        Assert.Empty(await context.DocFiles.ToListAsync());
    }

    [Fact]
    public async Task ImportedDocuments_CannotBeDeletedByRegeneration()
    {
        var repository = await Importer().ImportAsync(Zip(("doc.md", "keep")), "team", "keep");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RepositoryFullRegenerationCleaner().CleanAsync(context, repository));
        Assert.Equal("keep", (await context.DocFiles.SingleAsync()).Content);
    }

    [Fact]
    public async Task AnonymousImport_IsRejected()
    {
        user.SetupGet(u => u.UserId).Returns((string?)null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Importer().ImportAsync(Zip(("doc.md", "body")), "team", "private"));
    }

    [Fact]
    public async Task ScopedMcpSearch_DoesNotUseModelOrWorkspace()
    {
        await Importer().ImportAsync(Zip(("doc.md", "Camera sensor documentation")), "team", "docs");
        var server = new Mock<McpServer>();
        var options = new McpServerOptions();
        McpRepositoryScopeAccessor.SetScope(options, "team", "docs");
        server.SetupGet(s => s.ServerOptions).Returns(options);
        // Null AI dependencies fail immediately if the search attempts an AI summary.
        using var result = JsonDocument.Parse(await McpRepositoryTools.SearchDoc(context, null!, null!, server.Object,
            Options.Create(new RepositoryAnalyzerOptions()), "sensor"));
        Assert.Equal(1, result.RootElement.GetProperty("matchCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("summary").ValueKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateZipEntriesAndOversizedUpload_AreRejected(bool duplicate)
    {
        var file = duplicate ? Zip(("doc.md", "one"), ("DOC.md", "two")) :
            new FormFile(new MemoryStream(), 0, DocumentationImportService.MaxUploadBytes + 1, "archive", "large.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => Importer().ImportAsync(file, "team", "invalid"));
        Assert.Empty(await context.Repositories.ToListAsync());
    }

    private DocumentationImportService Importer() => new(context, user.Object, cache);

    private RepositoryDocsService Docs(IContext db) => new(db,
        new Mock<IGitPlatformService>(MockBehavior.Strict).Object, cache,
        new Mock<IGraphifyArtifactService>().Object, new RepositorySkillMarkdownBuilder(), user.Object);

    private static DocumentationArchive Snapshot() => new()
    {
        BranchName = "release/docs", LanguageCode = "zh", MindMapContent = "# 架构\n## 启动",
        Catalogs =
        [
            new() { Id = "remote-folder", Title = "架构", Path = "architecture", Order = 2 },
            new() { Id = "remote-doc", ParentId = "remote-folder", Title = "启动", Path = "architecture/start", Order = 7,
                Content = "# 启动\nCamera sensor starts here.\n", SourceFiles = "[\"src/main.cs\"]" }
        ]
    };

    private static IFormFile Zip(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return File(stream.ToArray());
    }

    private static IFormFile File(byte[] bytes) => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "archive", "docs.zip");
    public void Dispose() { context.Dispose(); connection.Dispose(); }
    private sealed class TestContext(DbContextOptions<TestContext> options) : MasterDbContext(options);
}
