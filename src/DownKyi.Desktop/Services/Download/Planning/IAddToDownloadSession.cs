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
    Task<DownloadSelectionResult> SelectDownloadAsync(
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

    Task<DownloadAddResult> AddToDownloadBatch(
        DownloadAddSelection selection,
        IReadOnlyList<FinalizedDownload> finalizedDownloads,
        CancellationToken cancellationToken = default);
}
