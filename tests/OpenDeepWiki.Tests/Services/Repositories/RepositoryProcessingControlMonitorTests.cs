using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class RepositoryProcessingControlMonitorTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    [Theory]
    [InlineData(RepositoryStatus.Paused)]
    [InlineData(RepositoryStatus.Stopped)]
    [InlineData(RepositoryStatus.Completed)]
    [InlineData(RepositoryStatus.Failed)]
    public async Task RunAsync_WhenStatusLeavesProcessing_TriggersCallbackOnce(RepositoryStatus controlledStatus)
    {
        var statuses = new Queue<RepositoryStatus?>(new[] { (RepositoryStatus?)RepositoryStatus.Processing, controlledStatus });
        var triggered = 0;
        var monitor = new RepositoryProcessingControlMonitor(
            _ => Task.FromResult(statuses.Count > 0 ? statuses.Dequeue() : RepositoryStatus.Processing),
            PollInterval);

        await monitor.RunAsync(() => Interlocked.Increment(ref triggered), CancellationToken.None);

        Assert.Equal(1, triggered);
    }

    [Fact]
    public async Task RunAsync_WhenStatusStaysProcessing_DoesNotTriggerAndExitsOnCancellation()
    {
        var reads = 0;
        using var cancellation = new CancellationTokenSource();
        var monitor = new RepositoryProcessingControlMonitor(
            _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<RepositoryStatus?>(RepositoryStatus.Processing);
            },
            PollInterval);

        var runTask = monitor.RunAsync(() => throw new InvalidOperationException("callback must not be invoked"), cancellation.Token);
        await Task.Delay(60);
        await cancellation.CancelAsync();
        await runTask;

        Assert.True(reads >= 2);
    }

    [Fact]
    public async Task RunAsync_WhenReadThrows_KeepsPollingAndTriggers()
    {
        var attempts = 0;
        var monitor = new RepositoryProcessingControlMonitor(
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("transient database failure");
                }

                return Task.FromResult<RepositoryStatus?>(RepositoryStatus.Stopped);
            },
            PollInterval);

        var triggered = false;
        await monitor.RunAsync(() => triggered = true, CancellationToken.None);

        Assert.True(triggered);
        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task RunAsync_WhenRepositoryMissing_DoesNotTrigger()
    {
        using var cancellation = new CancellationTokenSource();
        var monitor = new RepositoryProcessingControlMonitor(
            _ => Task.FromResult<RepositoryStatus?>(null),
            PollInterval);

        var runTask = monitor.RunAsync(() => throw new InvalidOperationException("callback must not be invoked"), cancellation.Token);
        await Task.Delay(40);
        await cancellation.CancelAsync();
        await runTask;
    }

    [Fact]
    public async Task RunAsync_WhenStoppingTokenAlreadyCancelled_ExitsWithoutReading()
    {
        var reads = 0;
        var monitor = new RepositoryProcessingControlMonitor(
            _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<RepositoryStatus?>(RepositoryStatus.Processing);
            },
            PollInterval);

        await monitor.RunAsync(() => throw new InvalidOperationException("callback must not be invoked"), new CancellationToken(canceled: true));

        Assert.Equal(0, reads);
    }
}
