using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Presentation;

namespace DownKyi.Services.Download;

internal interface IAddToDownloadSession
{
    Task<bool> EnsureAdmissionAsync(CancellationToken cancellationToken = default);

    Task<string?> SetDirectory(CancellationToken cancellationToken = default);

    void SetVideoInfoService(IInfoService videoInfoService);

    void GetVideo(VideoInfoView videoInfoView, IList<VideoSection> videoSections);

    void GetVideo();

    Task ParseVideoAsync(
        IInfoService videoInfoService,
        CancellationToken cancellationToken = default);

    Task<int> AddToDownload(
        string? directory,
        bool isAll = false,
        CancellationToken cancellationToken = default);
}
