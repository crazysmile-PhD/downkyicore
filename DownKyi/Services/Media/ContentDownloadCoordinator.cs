using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Services.Download;
using DownKyi.Services.Video;

namespace DownKyi.Services.Media;

internal enum DownloadInfoKind
{
    Video,
    Bangumi
}

internal sealed record ContentDownloadItem(string Source, DownloadInfoKind Kind, bool IsSelected);

internal interface IContentInfoServiceFactory
{
    Task<IInfoService> CreateAsync(ContentDownloadItem item, CancellationToken cancellationToken);
}

internal sealed class ContentInfoServiceFactory : IContentInfoServiceFactory
{
    private readonly ISettingsStore _settingsStore;
    private readonly IVideoTagProvider _tagProvider;
    private readonly IWbiKeyProvider _wbiKeyProvider;

    public ContentInfoServiceFactory(
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _tagProvider = tagProvider ?? throw new ArgumentNullException(nameof(tagProvider));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
    }

    public async Task<IInfoService> CreateAsync(
        ContentDownloadItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (item.Kind == DownloadInfoKind.Video)
        {
            return await VideoInfoService.CreateAsync(
                item.Source,
                _settingsStore,
                _tagProvider,
                _wbiKeyProvider,
                cancellationToken).ConfigureAwait(false);
        }

        return await Task.Run<IInfoService>(() => item.Kind switch
        {
            DownloadInfoKind.Bangumi => new BangumiInfoService(item.Source, _settingsStore, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, null)
        }, cancellationToken).ConfigureAwait(false);
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
                var infoService = await _infoServiceFactory
                    .CreateAsync(item, cancellationToken)
                    .ConfigureAwait(false);
                addToDownloadSession.SetVideoInfoService(infoService);
                addToDownloadSession.GetVideo();
                await addToDownloadSession
                    .ParseVideoAsync(infoService, cancellationToken)
                    .ConfigureAwait(false);
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
