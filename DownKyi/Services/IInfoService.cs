using System.Collections.Generic;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services;

public interface IInfoService
{
    VideoInfoView? GetVideoView(System.Threading.CancellationToken cancellationToken = default);

    IList<VideoSection>? GetVideoSections(bool noUgc, System.Threading.CancellationToken cancellationToken = default);

    IList<VideoPage>? GetVideoPages(System.Threading.CancellationToken cancellationToken = default);

    void GetVideoStream(VideoPage page, System.Threading.CancellationToken cancellationToken = default);
}
