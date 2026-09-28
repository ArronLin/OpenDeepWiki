using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class RepositoryProcessingControlServiceTests
{
    private const string RepositoryId = "repo-a";

    [Theory]
    [InlineData(RepositoryStatus.Pending)]
    [InlineData(RepositoryStatus.Processing)]
    public async Task PauseAsync_WhenStatusAllows_TransitionsToPaused(RepositoryStatus initialStatus)
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, initialStatus);
        await context.SaveChangesAsync();
        var service = CreateService();

        var result = await service.PauseAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Success, result.Outcome);
        Assert.Equal(RepositoryStatus.Paused, result.CurrentStatus);
        Assert.Equal(RepositoryStatus.Paused, await ReadStatusAsync(context));
    }

    [Theory]
    [InlineData(RepositoryStatus.Completed)]
    [InlineData(RepositoryStatus.Failed)]
    [InlineData(RepositoryStatus.Paused)]
    [InlineData(RepositoryStatus.Stopped)]
    public async Task PauseAsync_WhenStatusDisallowed_ReturnsInvalidState(RepositoryStatus initialStatus)
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, initialStatus);
        await context.SaveChangesAsync();
        var service = CreateService();

        var result = await service.PauseAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.InvalidState, result.Outcome);
        Assert.Equal(initialStatus, result.CurrentStatus);
        Assert.Equal(initialStatus, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task PauseAsync_WhenRepositoryMissing_ReturnsNotFound()
    {
        await using var context = CreateInMemoryContext();
        var service = CreateService();

        var result = await service.PauseAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.NotFound, result.Outcome);
        Assert.Null(result.CurrentStatus);
    }

    [Fact]
    public async Task ResumeAsync_WhenPaused_TransitionsToPending()
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Paused);
        await context.SaveChangesAsync();
        var service = CreateService();

        var result = await service.ResumeAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Success, result.Outcome);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
    }

    [Theory]
    [InlineData(RepositoryStatus.Pending)]
    [InlineData(RepositoryStatus.Processing)]
    [InlineData(RepositoryStatus.Completed)]
    [InlineData(RepositoryStatus.Failed)]
    [InlineData(RepositoryStatus.Stopped)]
    public async Task ResumeAsync_WhenStatusDisallowed_ReturnsInvalidState(RepositoryStatus initialStatus)
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, initialStatus);
        await context.SaveChangesAsync();
        var service = CreateService();

        var result = await service.ResumeAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.InvalidState, result.Outcome);
        Assert.Equal(initialStatus, await ReadStatusAsync(context));
    }

    [Theory]
    [InlineData(RepositoryStatus.Pending)]
    [InlineData(RepositoryStatus.Processing)]
    [InlineData(RepositoryStatus.Paused)]
    public async Task StopAsync_WhenStatusAllows_TransitionsToStopped(RepositoryStatus initialStatus)
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, initialStatus);
        await context.SaveChangesAsync();
        var service = CreateService();

        var result = await service.StopAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Success, result.Outcome);
        Assert.Equal(RepositoryStatus.Stopped, await ReadStatusAsync(context));
    }

    [Theory]
    [InlineData(RepositoryStatus.Completed)]
    [InlineData(RepositoryStatus.Failed)]
    [InlineData(RepositoryStatus.Stopped)]
    public async Task StopAsync_WhenStatusDisallowed_ReturnsInvalidState(RepositoryStatus initialStatus)
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, initialStatus);
        await context.SaveChangesAsync();
        var service = CreateService();

        var result = await service.StopAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.InvalidState, result.Outcome);
        Assert.Equal(initialStatus, await ReadStatusAsync(context));
    }

    [Theory]
    [InlineData(RepositoryStatus.Completed)]
    [InlineData(RepositoryStatus.Failed)]
    [InlineData(RepositoryStatus.Stopped)]
    public async Task RestartAsync_WhenNotProcessing_ClearsContentAndRequeues(RepositoryStatus initialStatus)
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, initialStatus);
        await SeedGeneratedContentAsync(context);
        var service = CreateService();

        var result = await service.RestartAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Success, result.Outcome);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
        await AssertContentClearedAsync(context);
    }

    [Fact]
    public async Task RestartAsync_WhenProcessingWithoutBoundLease_ClearsContentAndRequeues()
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Processing);
        await SeedGeneratedContentAsync(context);
        var service = CreateService();

        var result = await service.RestartAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Success, result.Outcome);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
        await AssertContentClearedAsync(context);
    }

    [Fact]
    public async Task RestartAsync_WhenProcessingWithHeldLease_WaitsForReleaseAndSucceeds()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var context = CreateInMemoryContext(databaseName);
        await SeedRepositoryAsync(context, RepositoryStatus.Processing);
        await SeedGeneratedContentAsync(context);
        SeedBoundLease(context);
        await context.SaveChangesAsync();
        var service = CreateService(releaseWaitInterval: TimeSpan.FromMilliseconds(25), releaseWaitTimeout: TimeSpan.FromSeconds(5));

        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(100);
            await using var releaseContext = CreateInMemoryContext(databaseName);
            await ReleaseLeaseAsync(releaseContext);
        });

        var result = await service.RestartAsync(context, RepositoryId);
        await releaseTask;

        Assert.Equal(RepositoryProcessingControlOutcome.Success, result.Outcome);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
        await AssertContentClearedAsync(context);
        Assert.False(await context.RepositoryGenerationLocks.AnyAsync(item => item.RepositoryId == RepositoryId));
        Assert.False(await context.WikiGenerationSlots.AnyAsync(item => item.RepositoryId == RepositoryId && item.InstanceId != null));
    }

    [Fact]
    public async Task RestartAsync_WhenProcessingLeaseNotReleased_ReturnsReleaseWaitTimedOut()
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Processing);
        await SeedGeneratedContentAsync(context);
        SeedBoundLease(context);
        await context.SaveChangesAsync();
        var service = CreateService(releaseWaitInterval: TimeSpan.FromMilliseconds(20), releaseWaitTimeout: TimeSpan.FromMilliseconds(150));

        var result = await service.RestartAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.ReleaseWaitTimedOut, result.Outcome);
        Assert.Equal(RepositoryStatus.Stopped, result.CurrentStatus);
        Assert.Equal(RepositoryStatus.Stopped, await ReadStatusAsync(context));
        Assert.NotEmpty(await context.DocCatalogs.ToListAsync());
    }

    [Fact]
    public async Task RestartAsync_WhenStateChangesDuringClear_ReturnsConflict()
    {
        await using var context = CreateInMemoryContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Completed);
        await context.SaveChangesAsync();
        var cleaner = new StubCleaner((_, repository, _) =>
        {
            repository.Status = RepositoryStatus.Processing;
            return Task.CompletedTask;
        });
        var service = new RepositoryProcessingControlService(cleaner);

        var result = await service.RestartAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task RestartAsync_WhenRepositoryMissing_ReturnsNotFound()
    {
        await using var context = CreateInMemoryContext();
        var service = CreateService();

        var result = await service.RestartAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task PauseAsync_WithSqliteProvider_ConditionalUpdatePath()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        await SeedRepositoryAsync(context, RepositoryStatus.Processing);
        await context.SaveChangesAsync();
        var service = CreateService();

        var result = await service.PauseAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Success, result.Outcome);
        Assert.Equal(RepositoryStatus.Paused, await ReadStatusAsync(context));
        var repository = await context.Repositories.AsNoTracking().SingleAsync(item => item.Id == RepositoryId);
        Assert.NotNull(repository.UpdatedAt);
    }

    [Fact]
    public async Task ResumeAsync_WithSqliteProvider_ConditionalUpdatePath()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        await SeedRepositoryAsync(context, RepositoryStatus.Paused);
        await context.SaveChangesAsync();
        var service = CreateService();

        var result = await service.ResumeAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Success, result.Outcome);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task RestartAsync_WithSqliteProvider_ClearsContentAndRequeues()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        await SeedRepositoryAsync(context, RepositoryStatus.Completed);
        await SeedGeneratedContentAsync(context);
        var service = CreateService();

        var result = await service.RestartAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Success, result.Outcome);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
        await AssertContentClearedAsync(context);
    }

    [Fact]
    public async Task RestartAsync_WithSqliteProvider_WhenStateChangesDuringClear_ReturnsConflict()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        await SeedRepositoryAsync(context, RepositoryStatus.Completed);
        await context.SaveChangesAsync();
        var cleaner = new StubCleaner((_, repository, _) =>
        {
            repository.Status = RepositoryStatus.Processing;
            return Task.CompletedTask;
        });
        var service = new RepositoryProcessingControlService(cleaner);

        var result = await service.RestartAsync(context, RepositoryId);

        Assert.Equal(RepositoryProcessingControlOutcome.Conflict, result.Outcome);
    }

    private static RepositoryProcessingControlService CreateService(
        TimeSpan? releaseWaitInterval = null,
        TimeSpan? releaseWaitTimeout = null)
    {
        return new RepositoryProcessingControlService(
            new RepositoryFullRegenerationCleaner(),
            releaseWaitInterval,
            releaseWaitTimeout);
    }

    private static TestDbContext CreateInMemoryContext(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static SqliteTestDbContext CreateSqliteContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SqliteTestDbContext>()
            .UseSqlite(connection)
            .Options;
        return new SqliteTestDbContext(options);
    }

    private static async Task SeedRepositoryAsync(IContext context, RepositoryStatus status)
    {
        context.Users.Add(new User
        {
            Id = "user-1",
            Name = "tester",
            Email = "tester@example.com"
        });
        await context.SaveChangesAsync();

        context.Repositories.Add(new Repository
        {
            Id = RepositoryId,
            OwnerUserId = "user-1",
            GitUrl = $"https://github.com/demo/{RepositoryId}.git",
            OrgName = "demo",
            RepoName = RepositoryId,
            Status = status
        });
        await context.SaveChangesAsync();
    }

    private static async Task SeedGeneratedContentAsync(IContext context)
    {
        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = "branch-1",
            RepositoryId = RepositoryId,
            BranchName = "main",
            LastCommitId = "abc123",
            LastProcessedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = "lang-1",
            RepositoryBranchId = "branch-1",
            LanguageCode = "en"
        });
        await context.SaveChangesAsync();

        context.DocFiles.Add(new DocFile
        {
            Id = "doc-1",
            BranchLanguageId = "lang-1",
            Content = "# document"
        });
        await context.SaveChangesAsync();

        context.DocCatalogs.Add(new DocCatalog
        {
            Id = "catalog-1",
            BranchLanguageId = "lang-1",
            Title = "Overview",
            Path = "overview",
            DocFileId = "doc-1"
        });
        context.RepositoryProcessingLogs.Add(new RepositoryProcessingLog
        {
            Id = "log-1",
            RepositoryId = RepositoryId,
            Message = "processed"
        });
        await context.SaveChangesAsync();
    }

    private static void SeedBoundLease(IContext context)
    {
        context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = RepositoryId,
            OwnerType = RepositoryGenerationLockOwnerType.Repository,
            OwnerId = RepositoryId,
            Scope = RepositoryGenerationLockScope.Repository,
            InstanceId = "instance-a",
            AcquiredAt = DateTime.UtcNow,
            HeartbeatAt = DateTime.UtcNow
        });
        context.WikiGenerationSlots.Add(new WikiGenerationSlot
        {
            Id = Guid.NewGuid().ToString(),
            SlotIndex = 0,
            WorkType = WikiGenerationWorkType.Repository,
            RepositoryId = RepositoryId,
            OwnerId = RepositoryId,
            InstanceId = "instance-a",
            AcquiredAt = DateTime.UtcNow,
            HeartbeatAt = DateTime.UtcNow
        });
    }

    private static async Task ReleaseLeaseAsync(IContext context)
    {
        var generationLock = await context.RepositoryGenerationLocks
            .FirstOrDefaultAsync(item => item.RepositoryId == RepositoryId);
        if (generationLock is not null)
        {
            context.RepositoryGenerationLocks.Remove(generationLock);
        }

        var slots = await context.WikiGenerationSlots
            .Where(item => item.RepositoryId == RepositoryId)
            .ToListAsync();
        foreach (var slot in slots)
        {
            slot.InstanceId = null;
            slot.RepositoryId = null;
            slot.OwnerId = null;
            slot.WorkType = null;
            slot.AcquiredAt = null;
            slot.HeartbeatAt = null;
            slot.UpdateTimestamp();
        }

        await context.SaveChangesAsync();
    }

    private static async Task<RepositoryStatus> ReadStatusAsync(IContext context)
    {
        return await context.Repositories
            .AsNoTracking()
            .Where(item => item.Id == RepositoryId)
            .Select(item => item.Status)
            .SingleAsync();
    }

    private static async Task AssertContentClearedAsync(IContext context)
    {
        Assert.Empty(await context.DocCatalogs.AsNoTracking().ToListAsync());
        Assert.Empty(await context.DocFiles.AsNoTracking().ToListAsync());
        Assert.Empty(await context.RepositoryProcessingLogs.AsNoTracking().ToListAsync());

        var branch = await context.RepositoryBranches.AsNoTracking().SingleAsync(item => item.Id == "branch-1");
        Assert.Null(branch.LastCommitId);
        Assert.Null(branch.LastProcessedAt);
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options);

    private sealed class SqliteTestDbContext(DbContextOptions<SqliteTestDbContext> options) : MasterDbContext(options);

    private sealed class StubCleaner(Func<IContext, Repository, CancellationToken, Task> handler) : IRepositoryFullRegenerationCleaner
    {
        public Task CleanAsync(IContext context, Repository repository, CancellationToken cancellationToken = default)
        {
            return handler(context, repository, cancellationToken);
        }
    }
}
