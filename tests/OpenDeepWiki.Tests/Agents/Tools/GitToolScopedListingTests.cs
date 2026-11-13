using OpenDeepWiki.Agents.Tools;
using Xunit;

namespace OpenDeepWiki.Tests.Agents.Tools;

public sealed class GitToolScopedListingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wiki-scoped-" + Guid.NewGuid());

    public GitToolScopedListingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "nested"));
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        File.WriteAllText(Path.Combine(_root, "src", "main.cpp"), "entry");
        File.WriteAllText(Path.Combine(_root, "src", "nested", "child.cpp"), "child");
        File.WriteAllText(Path.Combine(_root, "src", "ignored.cpp"), "ignored");
        File.WriteAllText(Path.Combine(_root, "src", ".gitignore"), "ignored.cpp\n");
        File.WriteAllText(Path.Combine(_root, "other", "other.cpp"), "other");
    }

    [Theory]
    [InlineData("src/**/*.cpp")]
    [InlineData("SRC/**/*.cpp")]
    [InlineData("src\\**\\*.cpp")]
    public async Task ScopedGlob_PreservesMatchingAndNestedIgnoreRules(string glob)
    {
        var files = await new GitTool(_root).ListFilesAsync(glob);
        Assert.Equal(new[] { "src/main.cpp", "src/nested/child.cpp" }, files);
    }

    [Fact]
    public async Task Limit_IsAppliedAfterIgnoreRules()
    {
        var files = await new GitTool(_root).ListFilesAsync("src/**/*", maxResults: 1);
        Assert.Equal(new[] { "src/main.cpp" }, files);
    }

    [Fact]
    public async Task ScopedSearch_FindsProductionContent()
    {
        var results = await new GitTool(_root).GrepAsync("child", "src/**/*.cpp");
        Assert.Single(results);
        Assert.Equal("src/nested/child.cpp", results[0].FilePath);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
