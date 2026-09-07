using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly IBilibiliApiClient _client;

    public ContentInfoServiceFactory(
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        IBilibiliApiClient client)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _tagProvider = tagProvider ?? throw new ArgumentNullException(nameof(tagProvider));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _client = client ?? throw new ArgumentNullException(nameof(client));
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
                _client,
                cancellationToken).ConfigureAwait(false);
        }

        return item.Kind switch
        {
            DownloadInfoKind.Bangumi => await BangumiInfoService.CreateAsync(
                item.Source,
                _settingsStore,
                _client,
                cancellationToken).ConfigureAwait(false),
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
    private readonly ILogger<ContentDownloadCoordinator> _logger;

    public ContentDownloadCoordinator(
        IAddToDownloadServiceFactory serviceFactory,
        IContentInfoServiceFactory infoServiceFactory)
        : this(serviceFactory, infoServiceFactory, NullLogger<ContentDownloadCoordinator>.Instance)
    {
    }

    public ContentDownloadCoordinator(
        IAddToDownloadServiceFactory serviceFactory,
        IContentInfoServiceFactory infoServiceFactory,
        ILogger<ContentDownloadCoordinator> logger)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        _infoServiceFactory = infoServiceFactory ?? throw new ArgumentNullException(nameof(infoServiceFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        _logger.LogInformationMessage($"Preparing {selectedItems.Length} item(s) for the download queue.");
        try
        {
            var addToDownloadSession = _serviceFactory.Create(ToPlayStreamType(selectedItems[0].Kind));
            var addedCount = await DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
                () => addToDownloadSession.SetDirectory(cancellationToken),
                directory => AddItemsAsync(
                    addToDownloadSession,
                    selectedItems,
                    directory,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            _logger.LogInformationMessage(addedCount == null
                ? "Download preparation stopped because no directory was selected."
                : $"Download preparation completed with {addedCount.Value} queued item(s).");
            return addedCount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformationMessage("Download preparation was canceled.");
            throw;
        }
    }

    private Task<int> AddItemsAsync(
        IAddToDownloadSession addToDownloadSession,
        ContentDownloadItem[] items,
        string directory,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var addedCount = 0;
            var processedCount = 0;
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
                processedCount++;
                _logger.LogDebugMessage($"Prepared download item {processedCount} of {items.Length}.");
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
