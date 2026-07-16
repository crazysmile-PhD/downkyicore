using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Logging;
using DownKyi.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services;

internal sealed class StorageMaintenanceHostedService : IHostedService
{
    private readonly ApplicationCancellation _applicationCancellation;
    private readonly ILogger<StorageMaintenanceHostedService> _logger;
    private Task? _maintenanceTask;

    public StorageMaintenanceHostedService(
        ApplicationCancellation applicationCancellation,
        ILogger<StorageMaintenanceHostedService> logger)
    {
        _applicationCancellation = applicationCancellation;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _maintenanceTask ??= RunMaintenanceAsync(_applicationCancellation.ShutdownToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _applicationCancellation.RequestShutdownAsync().ConfigureAwait(false);
        if (_maintenanceTask == null)
        {
            return;
        }

        await _maintenanceTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StorageManager.RunMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogErrorMessage("Storage maintenance failed.", e);
        }
    }
}
