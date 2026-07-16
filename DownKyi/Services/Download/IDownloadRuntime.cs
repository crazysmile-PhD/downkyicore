using System;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Download;

internal interface IDownloadRuntime : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
