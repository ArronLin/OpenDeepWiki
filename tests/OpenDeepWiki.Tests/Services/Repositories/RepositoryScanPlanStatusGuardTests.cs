using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class RepositoryScanPlanStatusGuardTests
{
    private const string RepositoryId = "repo-guard";

    [Fact]
    public async Task TryTransition_ThenFullEntityUpdate_KeepsProcessingStatusInDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connection).Options;

        await using (var seedContext = new TestDbContext(options))
        {
            await seedContext.Database.EnsureCreatedAsync();
            await SeedRepositoryAsync(seedContext, RepositoryStatus.Pending);
        }

        await using var workerContext = new TestDbContext(options);
        var tracked = await workerContext.Repositories.FirstAsync(item => item.Id == RepositoryId);

        var affected = await RepositoryStatusTransition.TryTransitionAsync(
            workerContext,
            RepositoryId,
            [RepositoryStatus.Pending, RepositoryStatus.Processing],
            RepositoryStatus.Processing,
            CancellationToken.None);

        Assert.Equal(1, affected);
        Assert.Equal(RepositoryStatus.Processing, tracked.Status);

        tracked.ScanProfileHash = "ABCDEF0123456789";
        tracked.UpdateTimestamp();
        workerContext.Repositories.Update(tracked);
        await workerContext.SaveChangesAsync();

        await using var verifyContext = new TestDbContext(options);
        var persisted = await verifyContext.Repositories
            .Where(item => item.Id == RepositoryId)
            .Select(item => item.Status)
            .FirstAsync();

        Assert.Equal(RepositoryStatus.Processing, persisted);
    }

    [Fact]
    public async Task ResolveAndEnsureAsync_OnTrackedProcessingRepository_DoesNotRevertStatus()
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "odw-scan-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workingDirectory, "Program.cs"),
                "public class Program { }");

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connection).Options;

            await using (var seedContext = new TestDbContext(options))
            {
                await seedContext.Database.EnsureCreatedAsync();
                await SeedRepositoryAsync(seedContext, RepositoryStatus.Pending);
            }

            await using var workerContext = new TestDbContext(options);
            var tracked = await workerContext.Repositories.FirstAsync(item => item.Id == RepositoryId);

            var affected = await RepositoryStatusTransition.TryTransitionAsync(
                workerContext,
                RepositoryId,
                [RepositoryStatus.Pending, RepositoryStatus.Processing],
                RepositoryStatus.Processing,
                CancellationToken.None);
            Assert.Equal(1, affected);

            var resolver = new RepositoryScanPlanResolver(
                new StaticOptionsMonitor<WikiGeneratorOptions>(new WikiGeneratorOptions()));

            await resolver.ResolveAndEnsureAsync(
                workerContext,
                tracked,
                workingDirectory,
                CancellationToken.None);

            await using var verifyContext = new TestDbContext(options);
            var persisted = await verifyContext.Repositories
                .Where(item => item.Id == RepositoryId)
                .Select(item => item.Status)
                .FirstAsync();

            Assert.Equal(RepositoryStatus.Processing, persisted);
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task SeedRepositoryAsync(IContext context, RepositoryStatus status)
    {
        context.Users.Add(new User
        {
            Id = "user-guard",
            Name = "guard-tester",
            Email = "guard@example.com"
        });
        await context.SaveChangesAsync();

        context.Repositories.Add(new Repository
        {
            Id = RepositoryId,
            OwnerUserId = "user-guard",
            GitUrl = "https://github.com/demo/guard.git",
            OrgName = "demo",
            RepoName = "guard",
            Status = status
        });
        await context.SaveChangesAsync();
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
