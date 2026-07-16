using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Services.Download;

namespace DownKyi.Services.Media;

internal enum DownloadInfoKind
{
    Video,
    Bangumi
}

internal sealed record ContentDownloadItem(string Source, DownloadInfoKind Kind, bool IsSelected);

internal interface IContentInfoServiceFactory
{
    IInfoService Create(ContentDownloadItem item, CancellationToken cancellationToken);
}

internal sealed class ContentInfoServiceFactory : IContentInfoServiceFactory
{
    private readonly ISettingsStore _settingsStore;

    public ContentInfoServiceFactory(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public IInfoService Create(ContentDownloadItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        return item.Kind switch
        {
            DownloadInfoKind.Video => new VideoInfoService(item.Source, _settingsStore, cancellationToken),
            DownloadInfoKind.Bangumi => new BangumiInfoService(item.Source, _settingsStore, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, null)
        };
    }
}

internal interface IContentDownloadCoordinator
{
    Task<int?> AddAsync(
        IReadOnlyList<ContentDownloadItem> items,
        bool onlySelected,
        CancellationToken cancellationToken);
}

internal sealed class ContentDownloadCoordinator : IContentDownloadCoordinator
{
    private readonly IAddToDownloadServiceFactory _serviceFactory;
    private readonly IContentInfoServiceFactory _infoServiceFactory;

    public ContentDownloadCoordinator(
        IAddToDownloadServiceFactory serviceFactory,
        IContentInfoServiceFactory infoServiceFactory)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        _infoServiceFactory = infoServiceFactory ?? throw new ArgumentNullException(nameof(infoServiceFactory));
    }

    public async Task<int?> AddAsync(
        IReadOnlyList<ContentDownloadItem> items,
        bool onlySelected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedItems = onlySelected
            ? items.Where(item => item.IsSelected).ToArray()
            : items.ToArray();
        if (selectedItems.Length == 0)
        {
            return 0;
        }

        var addToDownloadSession = _serviceFactory.Create(ToPlayStreamType(selectedItems[0].Kind));
        return await DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
            () => addToDownloadSession.SetDirectory(cancellationToken),
            directory => AddItemsAsync(
                addToDownloadSession,
                selectedItems,
                directory,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
    }

    private Task<int> AddItemsAsync(
        IAddToDownloadSession addToDownloadSession,
        IReadOnlyList<ContentDownloadItem> items,
        string directory,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var addedCount = 0;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var infoService = _infoServiceFactory.Create(item, cancellationToken);
                addToDownloadSession.SetVideoInfoService(infoService);
                addToDownloadSession.GetVideo();
                addToDownloadSession.ParseVideo(infoService);
                cancellationToken.ThrowIfCancellationRequested();
                addedCount += await addToDownloadSession
                    .AddToDownload(directory, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            return addedCount;
        }, cancellationToken);
    }

    private static PlayStreamType ToPlayStreamType(DownloadInfoKind kind)
    {
        return kind switch
        {
            DownloadInfoKind.Video => PlayStreamType.Video,
            DownloadInfoKind.Bangumi => PlayStreamType.Bangumi,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}
