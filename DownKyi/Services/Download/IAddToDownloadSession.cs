using System.Collections.Generic;
using System.Threading.Tasks;
using DownKyi.PrismExtension.Dialog;
using DownKyi.ViewModels.PageViewModels;
using Prism.Events;

namespace DownKyi.Services.Download;

internal interface IAddToDownloadSession
{
    Task<string?> SetDirectory(IDialogService? dialogService);

    void SetVideoInfoService(IInfoService videoInfoService);

    void GetVideo(VideoInfoView videoInfoView, IList<VideoSection> videoSections);

    void GetVideo();

    void ParseVideo(IInfoService videoInfoService);

    Task<int> AddToDownload(
        IEventAggregator eventAggregator,
        IDialogService? dialogService,
        string? directory,
        bool isAll = false);
}
