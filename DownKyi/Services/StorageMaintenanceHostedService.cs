using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Logging;
using DownKyi.Core.Storage;
using Microsoft.Extensions.Hosting;

namespace DownKyi.Services;

internal sealed class StorageMaintenanceHostedService : IHostedService
{
    private readonly ApplicationCancellation _applicationCancellation;
    private Task? _maintenanceTask;

    public StorageMaintenanceHostedService(ApplicationCancellation applicationCancellation)
    {
        _applicationCancellation = applicationCancellation;
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

    private static async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StorageManager.RunMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogManager.Error(nameof(StorageManager), e);
        }
    }
}
