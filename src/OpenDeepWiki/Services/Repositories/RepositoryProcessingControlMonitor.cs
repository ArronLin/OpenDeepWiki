using Microsoft.Extensions.Logging;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Watches the persisted status of a repository while its processing job runs.
/// The database is the single source of truth for user control requests, so the
/// monitor polls it and raises a callback once the status is no longer
/// <see cref="RepositoryStatus.Processing"/>. Query failures are tolerated so a
/// transient database error cannot abort the running job.
/// </summary>
public sealed class RepositoryProcessingControlMonitor
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly Func<CancellationToken, Task<RepositoryStatus?>> _readStatusAsync;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger? _logger;

    public RepositoryProcessingControlMonitor(
        Func<CancellationToken, Task<RepositoryStatus?>> readStatusAsync,
        TimeSpan? pollInterval = null,
        ILogger? logger = null)
    {
        _readStatusAsync = readStatusAsync;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _logger = logger;
    }

    public async Task RunAsync(Action onControlRequested, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            RepositoryStatus? status = null;
            try
            {
                status = await _readStatusAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Repository processing control monitor failed to read status");
            }

            if (status is not null && status != RepositoryStatus.Processing)
            {
                onControlRequested();
                return;
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
