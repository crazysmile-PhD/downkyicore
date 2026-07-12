using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal interface IDownloadService : IDisposable
{
    Task ParseAsync(DownloadingItem downloading);
    string? DownloadAudio(DownloadingItem downloading);
    string? DownloadVideo(DownloadingItem downloading);
    Task<string> DownloadDanmakuAsync(DownloadingItem downloading);
    Task<IReadOnlyList<string>> DownloadSubtitleAsync(DownloadingItem downloading);
    Task<string?> DownloadCoverAsync(DownloadingItem downloading, string? coverUrl, string fileName);
    string? MixedFlow(DownloadingItem downloading, string? audioUid, string? videoUid);

    Task StartAsync(CancellationToken cancellationToken = default);
    Task EndAsync();
}
