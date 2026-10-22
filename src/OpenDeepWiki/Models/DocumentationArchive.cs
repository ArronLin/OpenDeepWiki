using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Models;

/// <summary>Portable, source-free snapshot of one branch and documentation language.</summary>
public sealed class DocumentationArchive
{
    public const string EntryName = "opendeepwiki.json";
    public string Format { get; set; } = "opendeepwiki-documents";
    public int Version { get; set; } = 1;
    public string BranchName { get; set; } = "main";
    public string LanguageCode { get; set; } = "en";
    public string? MindMapContent { get; set; }
    public List<DocumentationArchiveCatalog> Catalogs { get; set; } = [];

    public static DocumentationArchive Create(RepositoryBranch branch, BranchLanguage language,
        IEnumerable<DocCatalog> catalogs) => new()
    {
        BranchName = branch.BranchName,
        LanguageCode = language.LanguageCode,
        MindMapContent = language.MindMapContent,
        Catalogs = catalogs.Select(c => new DocumentationArchiveCatalog
        {
            Id = c.Id, ParentId = c.ParentId, Title = c.Title, Path = c.Path, Order = c.Order,
            Content = c.DocFile?.Content, SourceFiles = c.DocFile?.SourceFiles
        }).ToList()
    };
}

public sealed class DocumentationArchiveCatalog
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Order { get; set; }
    public string? Content { get; set; }
    public string? SourceFiles { get; set; }
}
