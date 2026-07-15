using System;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal interface IAddToDownloadServiceFactory
{
    AddToDownloadService Create(PlayStreamType streamType);

    AddToDownloadService Create(string id, PlayStreamType streamType);
}

internal sealed class AddToDownloadServiceFactory : IAddToDownloadServiceFactory
{
    private readonly DownloadListState _downloadLists;
    private readonly DownloadStorageService _downloadStorageService;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<AddToDownloadService> _logger;

    public AddToDownloadServiceFactory(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService,
        ISettingsStore settingsStore,
        ILogger<AddToDownloadService> logger)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _downloadStorageService = downloadStorageService ?? throw new ArgumentNullException(nameof(downloadStorageService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AddToDownloadService Create(PlayStreamType streamType)
    {
        return new AddToDownloadService(streamType, _downloadLists, _downloadStorageService, _settingsStore, _logger);
    }

    public AddToDownloadService Create(string id, PlayStreamType streamType)
    {
        return new AddToDownloadService(id, streamType, _downloadLists, _downloadStorageService, _settingsStore, _logger);
    }
}
