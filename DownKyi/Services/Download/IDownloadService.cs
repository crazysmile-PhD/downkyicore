using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

public interface IDownloadService : IDisposable
{
    void Parse(DownloadingItem downloading);
    string? DownloadAudio(DownloadingItem downloading);
    string? DownloadVideo(DownloadingItem downloading);
    string DownloadDanmaku(DownloadingItem downloading);
    List<string> DownloadSubtitle(DownloadingItem downloading);
    string? DownloadCover(DownloadingItem downloading, string? coverUrl, string fileName);
    string? MixedFlow(DownloadingItem downloading, string? audioUid, string? videoUid);

    Task StartAsync(CancellationToken cancellationToken = default);
    Task EndAsync();
}
