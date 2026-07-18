using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Logging;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskStateWriter
{
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly ILogger _logger;

    public DownloadTaskStateWriter(DownloadTaskProjectionStore projectionStore, ILogger logger)
    {
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task UpdateAsync(
        DownloadingItem downloading,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        try
        {
            await _projectionStore
                .UpdateDownloadingAsync(downloading, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException e)
        {
            _logger.LogDebugMessage($"Persist downloading state failed: {e.Message}");
        }
        catch (InvalidOperationException e)
        {
            _logger.LogDebugMessage($"Persist downloading state conflicted: {e.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }
}
