using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;

namespace DownKyi.Services.Download;

internal interface IAddToDownloadSession
{
    Task<bool> EnsureAdmissionAsync(CancellationToken cancellationToken = default);

    Task<DownloadAddSelection?> SelectDownloadAsync(
        VideoPage? subtitlePage = null,
        CancellationToken cancellationToken = default);

    Task<PreparedDownload> PrepareAsync(
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        DownloadContentSelection requestedContent,
        bool isAll,
        CancellationToken cancellationToken = default);

    Task<PreparedDownload?> PrepareAsync(
        IInfoService videoInfoService,
        DownloadContentSelection requestedContent,
        CancellationToken cancellationToken = default);

    Task<DownloadAddResult> AddToDownload(
        string directory,
        FinalizedDownload finalizedDownload,
        CancellationToken cancellationToken = default);
}
