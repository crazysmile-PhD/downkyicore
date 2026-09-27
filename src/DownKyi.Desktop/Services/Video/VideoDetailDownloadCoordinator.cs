using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Presentation;
using DownKyi.Services.Download;

namespace DownKyi.Services.Video;

internal interface IVideoDetailDownloadCoordinator
{
    Task<int?> AddAsync(
        string input,
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        bool isAll,
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoInfoView);
        ArgumentNullException.ThrowIfNull(videoSections);
        cancellationToken.ThrowIfCancellationRequested();

        var streamType = PlayStreamTypeResolver.ResolvePlayStreamType(input);
        if (streamType == null)
        {
            return Task.FromResult<int?>(null);
        }

        var addService = _serviceFactory.Create(streamType.Value);
        return DownloadAddCoordinator.AddToDownloadIfSelectionAcceptedAsync(
            () => addService.EnsureAdmissionAsync(cancellationToken),
            () => addService.SelectDownloadAsync(cancellationToken),
            async selection =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preparedDownload = await addService
                    .PrepareAsync(videoInfoView, videoSections, isAll, cancellationToken)
                    .ConfigureAwait(false);
                return await addService
                    .AddToDownload(selection, preparedDownload, isAll, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }
}
