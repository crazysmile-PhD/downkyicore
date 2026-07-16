using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services.Download;

internal interface IAddToDownloadSession
{
    Task<string?> SetDirectory(CancellationToken cancellationToken = default);

    void SetVideoInfoService(IInfoService videoInfoService);

    void GetVideo(VideoInfoView videoInfoView, IList<VideoSection> videoSections);

    void GetVideo();

    void ParseVideo(IInfoService videoInfoService);

    Task<int> AddToDownload(
        string? directory,
        bool isAll = false,
        CancellationToken cancellationToken = default);
}
