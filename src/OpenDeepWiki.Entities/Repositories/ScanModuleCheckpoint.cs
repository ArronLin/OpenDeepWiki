namespace OpenDeepWiki.Entities;

public sealed class ScanModuleCheckpoint
{
    public string Id { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string VersionKey { get; set; } = string.Empty;
    public string ModuleRoot { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
    public string ExcerptsJson { get; set; } = string.Empty;
}
