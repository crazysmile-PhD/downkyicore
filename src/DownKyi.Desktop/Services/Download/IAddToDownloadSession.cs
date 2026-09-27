using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
using DownKyi.Presentation;

namespace DownKyi.Services.Download;

internal interface IAddToDownloadSession
{
    Task<bool> EnsureAdmissionAsync(CancellationToken cancellationToken = default);

    Task<DownloadAddSelection?> SelectDownloadAsync(CancellationToken cancellationToken = default);

    Task<PreparedDownload> PrepareAsync(
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        bool isAll,
        CancellationToken cancellationToken = default);

    Task<PreparedDownload?> PrepareAsync(
        IInfoService videoInfoService,
        CancellationToken cancellationToken = default);

    Task<int> AddToDownload(
        DownloadAddSelection selection,
        PreparedDownload preparedDownload,
        bool isAll = false,
        CancellationToken cancellationToken = default);
}
