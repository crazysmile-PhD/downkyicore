using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed record DownloadTransferRequest(
    DownloadingItem Download,
    IReadOnlyList<string> Urls,
    string Directory,
    string FileName,
    long ExpectedBytes,
    Action EnsureActive,
    Func<CancellationToken, Task> PersistStateAsync,
    CancellationToken CancellationToken);

internal interface ITransferBackend : IDisposable
{
    string Name { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task<DownloadTransferOutcome> TransferAsync(DownloadTransferRequest request);
}
