using System.Collections.Generic;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services;

internal interface IInfoService
{
    VideoInfoView? GetVideoView(System.Threading.CancellationToken cancellationToken = default);

    IList<VideoSection>? GetVideoSections(bool noUgc, System.Threading.CancellationToken cancellationToken = default);

    IList<VideoPage>? GetVideoPages(System.Threading.CancellationToken cancellationToken = default);

    PlayUrl? GetVideoStream(VideoPage page, System.Threading.CancellationToken cancellationToken = default);
}
