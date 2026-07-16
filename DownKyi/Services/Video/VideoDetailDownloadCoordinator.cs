using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services.Download;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services.Video;

internal interface IVideoDetailDownloadCoordinator
{
    Task<int?> AddAsync(
        string input,
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        bool isAll,
        IDialogService? dialogService,
        CancellationToken cancellationToken);
}

internal sealed class VideoDetailDownloadCoordinator : IVideoDetailDownloadCoordinator
{
    private readonly IAddToDownloadServiceFactory _serviceFactory;

    public VideoDetailDownloadCoordinator(IAddToDownloadServiceFactory serviceFactory)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
    }

    public Task<int?> AddAsync(
        string input,
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        bool isAll,
        IDialogService? dialogService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoInfoView);
        ArgumentNullException.ThrowIfNull(videoSections);
        cancellationToken.ThrowIfCancellationRequested();

        var streamType = VideoInputResolver.ResolvePlayStreamType(input);
        if (streamType == null)
        {
            return Task.FromResult<int?>(null);
        }

        var addService = _serviceFactory.Create(streamType.Value);
        return DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
            () => addService.SetDirectory(dialogService),
            async directory =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                addService.GetVideo(videoInfoView, videoSections);
                return await addService
                    .AddToDownload(dialogService, directory, isAll)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }
}
