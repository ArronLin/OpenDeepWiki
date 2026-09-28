using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class RepositoryProcessingWorkerControlTests
{
    private const string RepositoryId = "repo-a";
    private const string InstanceId = "test-instance";

    [Fact]
    public async Task ProcessRepositoryJobAsync_WhenStatusNoLongerSchedulable_AbandonsAndReleasesLease()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var harness = await WorkerHarness.CreateInMemoryAsync(databaseName);
        using var context = harness.CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Paused);

        var lease = await AcquireLeaseAsync(harness);

        await harness.Worker.ProcessRepositoryJobAsync(RepositoryId, lease, CancellationToken.None);

        Assert.Equal(RepositoryStatus.Paused, await ReadStatusAsync(context));
        harness.BranchProcessor.Verify(
            processor => processor.ProcessBranchAsync(
                It.IsAny<IContext>(),
                It.IsAny<Repository>(),
                It.IsAny<RepositoryBranch>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        await AssertLeaseReleasedAsync(context);
    }

    [Fact]
    public async Task ProcessRepositoryJobAsync_WhenStoppingTokenCancels_WritesPendingAndRethrows()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var harness = await WorkerHarness.CreateInMemoryAsync(databaseName);
        using var context = harness.CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Pending);
        await SeedBranchAsync(context);
        harness.HangBranchProcessor();

        var lease = await AcquireLeaseAsync(harness);
        using var cancellation = new CancellationTokenSource();
        var jobTask = harness.Worker.ProcessRepositoryJobAsync(RepositoryId, lease, cancellation.Token);
        await WaitForStatusAsync(context, RepositoryStatus.Processing);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobTask);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
        await AssertLeaseReleasedAsync(context);
    }

    [Fact]
    public async Task ProcessRepositoryJobAsync_WhenUserControlledMidProcessing_KeepsUserStatusAndReleasesLease()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var harness = await WorkerHarness.CreateInMemoryAsync(databaseName);
        using var context = harness.CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Pending);
        await SeedBranchAsync(context);
        harness.HangBranchProcessor();

        var lease = await AcquireLeaseAsync(harness);
        var jobTask = harness.Worker.ProcessRepositoryJobAsync(RepositoryId, lease, CancellationToken.None);
        await WaitForStatusAsync(context, RepositoryStatus.Processing);

        using (var controlContext = harness.CreateContext())
        {
            await SetStatusAsync(controlContext, RepositoryStatus.Stopped);
        }

        await jobTask;

        Assert.Equal(RepositoryStatus.Stopped, await ReadStatusAsync(context));
        await AssertLeaseReleasedAsync(context);
    }

    [Fact]
    public async Task ProcessRepositoryJobAsync_WhenStatusChangesDuringProcessing_DoesNotOverwriteFinalStatus()
    {
        var databasePath = CreateSqlitePath();
        try
        {
            await using var harness = await WorkerHarness.CreateSqliteAsync(databasePath, controlPollInterval: TimeSpan.FromMinutes(10));
            using var context = harness.CreateContext();
            await SeedRepositoryAsync(context, RepositoryStatus.Pending);
            await SeedBranchAsync(context);

            harness.BranchProcessor
                .Setup(processor => processor.ProcessBranchAsync(
                    It.IsAny<IContext>(),
                    It.IsAny<Repository>(),
                    It.IsAny<RepositoryBranch>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (IContext _, Repository _, RepositoryBranch _, string? _, bool _, CancellationToken _) =>
                {
                    using var controlContext = harness.CreateContext();
                    await SetStatusAsync(controlContext, RepositoryStatus.Stopped);
                    return (string?)null;
                });

            var lease = await AcquireLeaseAsync(harness);

            await harness.Worker.ProcessRepositoryJobAsync(RepositoryId, lease, CancellationToken.None);

            Assert.Equal(RepositoryStatus.Stopped, await ReadStatusAsync(context));
            await AssertLeaseReleasedAsync(context);
        }
        finally
        {
            DeleteSqliteDatabase(databasePath);
        }
    }

    private static async Task<WikiGenerationWorkLease> AcquireLeaseAsync(WorkerHarness harness)
    {
        using var scope = harness.Provider.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IWikiGenerationCoordinator>();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();

        var (status, lease) = await coordinator.TryBeginAsync(
            context,
            RepositoryId,
            RepositoryGenerationLockOwnerType.Repository,
            RepositoryId,
            RepositoryGenerationLockScope.Repository,
            WikiGenerationWorkType.Repository);

        Assert.Equal(WikiGenerationAcquireStatus.Acquired, status);
        Assert.NotNull(lease);
        return lease!;
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

    private static async Task SeedBranchAsync(IContext context)
    {
        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = "branch-1",
            RepositoryId = RepositoryId,
            BranchName = "main"
        });
        await context.SaveChangesAsync();
    }

    private static async Task SetStatusAsync(IContext context, RepositoryStatus status)
    {
        var repository = await context.Repositories.FirstAsync(item => item.Id == RepositoryId);
        repository.Status = status;
        context.Repositories.Update(repository);
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

    private static async Task WaitForStatusAsync(IContext context, RepositoryStatus expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await ReadStatusAsync(context) == expected)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Repository did not reach status '{expected}' in time.");
    }

    private static async Task AssertLeaseReleasedAsync(IContext context)
    {
        Assert.False(await context.RepositoryGenerationLocks
            .AsNoTracking()
            .AnyAsync(item => item.RepositoryId == RepositoryId));
        Assert.False(await context.WikiGenerationSlots
            .AsNoTracking()
            .AnyAsync(item => item.RepositoryId == RepositoryId && item.InstanceId != null));
    }

    private static string CreateSqlitePath()
    {
        return Path.Combine(Path.GetTempPath(), $"opendeepwiki-worker-control-{Guid.NewGuid():N}.db");
    }

    private static void DeleteSqliteDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        try
        {
            File.Delete(databasePath);
        }
        catch (IOException)
        {
            // SQLite can keep a file handle alive briefly after context disposal.
        }
    }

    private sealed class WorkerHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly List<IServiceScope> _scopes = new();

        private WorkerHarness(
            ServiceProvider provider,
            RepositoryProcessingWorker worker,
            Mock<IRepositoryBranchProcessor> branchProcessor)
        {
            _provider = provider;
            Worker = worker;
            BranchProcessor = branchProcessor;
        }

        public RepositoryProcessingWorker Worker { get; }

        public Mock<IRepositoryBranchProcessor> BranchProcessor { get; }

        public IServiceProvider Provider => _provider;

        public static Task<WorkerHarness> CreateInMemoryAsync(string databaseName, TimeSpan? controlPollInterval = null)
        {
            return CreateAsync(
                () => new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
                    .UseInMemoryDatabase(databaseName)
                    .Options),
                initialize: null,
                controlPollInterval);
        }

        public static Task<WorkerHarness> CreateSqliteAsync(string databasePath, TimeSpan? controlPollInterval = null)
        {
            return CreateAsync(
                () => new SqliteTestDbContext(new DbContextOptionsBuilder<SqliteTestDbContext>()
                    .UseSqlite($"Data Source={databasePath}")
                    .Options),
                initialize: context => ((DbContext)context).Database.EnsureCreatedAsync(),
                controlPollInterval);
        }

        public IContext CreateContext()
        {
            var scope = _provider.CreateScope();
            _scopes.Add(scope);
            return scope.ServiceProvider.GetRequiredService<IContext>();
        }

        public void HangBranchProcessor()
        {
            BranchProcessor
                .Setup(processor => processor.ProcessBranchAsync(
                    It.IsAny<IContext>(),
                    It.IsAny<Repository>(),
                    It.IsAny<RepositoryBranch>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (IContext _, Repository _, RepositoryBranch _, string? _, bool _, CancellationToken cancellationToken) =>
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return (string?)null;
                });
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var scope in _scopes)
            {
                scope.Dispose();
            }

            await _provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }

        private static async Task<WorkerHarness> CreateAsync(
            Func<IContext> contextFactory,
            Func<IContext, Task>? initialize,
            TimeSpan? controlPollInterval)
        {
            var branchProcessor = new Mock<IRepositoryBranchProcessor>();
            var repositoryAnalyzer = new Mock<IRepositoryAnalyzer>();
            var wikiGenerator = new Mock<IWikiGenerator>();
            var optionsMonitor = new StaticOptionsMonitor<WikiGeneratorOptions>(new WikiGeneratorOptions
            {
                MaxConcurrentGenerations = 1
            });
            var identity = new WikiGenerationInstanceIdentity(InstanceId);

            var services = new ServiceCollection();
            services.AddScoped(_ => contextFactory());
            services.AddSingleton(identity);
            services.AddSingleton<IOptionsMonitor<WikiGeneratorOptions>>(optionsMonitor);
            services.AddScoped(serviceProvider => new RepositoryGenerationLockService(
                serviceProvider.GetRequiredService<IContext>(),
                identity,
                optionsMonitor));
            services.AddScoped<IRepositoryGenerationLockService>(
                serviceProvider => serviceProvider.GetRequiredService<RepositoryGenerationLockService>());
            services.AddScoped(serviceProvider => new WikiGenerationCoordinator(
                serviceProvider.GetRequiredService<IRepositoryGenerationLockService>(),
                identity,
                optionsMonitor));
            services.AddScoped<IWikiGenerationCoordinator>(
                serviceProvider => serviceProvider.GetRequiredService<WikiGenerationCoordinator>());
            services.AddScoped(_ => repositoryAnalyzer.Object);
            services.AddScoped(_ => wikiGenerator.Object);
            services.AddScoped(_ => branchProcessor.Object);

            var provider = services.BuildServiceProvider();

            if (initialize is not null)
            {
                using var scope = provider.CreateScope();
                await initialize(scope.ServiceProvider.GetRequiredService<IContext>());
            }

            var worker = new RepositoryProcessingWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<RepositoryProcessingWorker>.Instance,
                optionsMonitor)
            {
                ControlPollInterval = controlPollInterval ?? TimeSpan.FromMilliseconds(20)
            };

            return new WorkerHarness(provider, worker, branchProcessor);
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options);

    private sealed class SqliteTestDbContext(DbContextOptions<SqliteTestDbContext> options) : MasterDbContext(options);

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
