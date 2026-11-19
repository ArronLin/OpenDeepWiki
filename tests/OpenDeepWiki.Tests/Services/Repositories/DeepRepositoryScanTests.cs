using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public sealed class DeepRepositoryScanTests
{
    [Fact]
    public void CommitInventoryUsesCommittedIgnoreRulesAndExcludesUncommittedPaths()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            LibGit2Sharp.Repository.Init(root.FullName);
            using var git = new LibGit2Sharp.Repository(root.FullName);
            Directory.CreateDirectory(Path.Combine(root.FullName, "mod"));
            foreach (var path in new[] { "mod/main.cs", "mod/generated.cs", "mod/logo.png", "mod/private.pem" })
                File.WriteAllText(Path.Combine(root.FullName, path), "fixture");
            File.WriteAllText(Path.Combine(root.FullName, "mod/empty.txt"), "");
            LibGit2Sharp.Commands.Stage(git, "*");
            var ignores = Path.Combine(root.FullName, "mod", ".gitignore");
            File.WriteAllText(ignores, "generated.cs");
            LibGit2Sharp.Commands.Stage(git, "mod/.gitignore");
            var author = new LibGit2Sharp.Signature("fixture", "test@example.org", DateTimeOffset.UtcNow);
            var commit = git.Commit("fixture", author, author, new LibGit2Sharp.CommitOptions());
            File.WriteAllText(ignores, "main.cs");
            File.WriteAllText(Path.Combine(root.FullName, "draft.cs"), "untracked");
            var snapshot = new RepositoryWorkspace
            {
                WorkingDirectory = root.FullName, GitObjectDirectory = root.FullName, CommitId = commit.Sha
            };
            var inventory = DeepRepositoryScan.InventoryCommit(snapshot, default);
            Assert.Equal(new[] { "mod/main.cs" }, inventory.Files);
            Assert.Equal(1, inventory.Excluded["empty"]);
        }
        finally
        {
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes &= ~FileAttributes.ReadOnly;
            root.Delete(true);
        }
    }

    [Fact]
    public void ModelCanOnlyAddFilesFromTheCurrentModuleCatalog()
    {
        Assert.Equal(new[] { "mod/b", "mod/a" }, DeepRepositoryScan.SelectRequiredFiles(
            ["mod/a", "mod/b"], [1, -1, 99, 1], ["mod/a"]));
    }

    [Fact]
    public async Task SnapshotChangesWhenContentChangesWithTheSameLengthAndTimestamp()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(root.FullName, "a.txt");
            File.WriteAllText(file, "old");
            var time = File.GetLastWriteTimeUtc(file);
            var original = await RepositoryAnalyzer.ComputeDirectorySnapshotIdAsync(root.FullName, default);
            File.WriteAllText(file, "new");
            File.SetLastWriteTimeUtc(file, time);
            Assert.NotEqual(original, await RepositoryAnalyzer.ComputeDirectorySnapshotIdAsync(root.FullName, default));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void InventoryKeepsDeepSourceAndSkipsGeneratedAndCredentialFiles()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var files = new[]
            {
                "packages/app/src/feature/internal/workflow/deep.cs",
                "build/tasks/generate.py", "vendor/owned/adapter.go",
                "node_modules/dependency/index.js", "src/certificate.pem", "src/.env.local", "src/logo.png"
            };
            foreach (var relative in files)
            {
                var path = Path.Combine(root.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture");
            }

            File.WriteAllText(Path.Combine(root.FullName, "empty.txt"), "");
            var result = DeepRepositoryScan.Inventory(root.FullName, CancellationToken.None);
            Assert.Equal(new[] { files[1], files[0], files[2] }, result.Files);
            Assert.Equal(1, result.Excluded["empty"]);
            Assert.Equal(2, result.Excluded["credentials"]);
            Assert.Equal(1, result.Excluded["dependencies-or-generated"]);
            Assert.Equal(1, result.Excluded["binary-or-generated"]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
