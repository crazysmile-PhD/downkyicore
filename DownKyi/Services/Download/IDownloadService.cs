using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal interface IDownloadService : IDisposable
{
    Task ParseAsync(DownloadingItem downloading);
    Task<string?> DownloadAudioAsync(DownloadingItem downloading);
    Task<string?> DownloadVideoAsync(DownloadingItem downloading);
    Task<string> DownloadDanmakuAsync(DownloadingItem downloading);
    Task<IReadOnlyList<string>> DownloadSubtitleAsync(DownloadingItem downloading);
    Task<string?> DownloadCoverAsync(DownloadingItem downloading, string? coverUrl, string fileName);
    Task<string?> MixedFlowAsync(DownloadingItem downloading, string? audioUid, string? videoUid);

    Task StartAsync(CancellationToken cancellationToken = default);
    Task EndAsync();
}
