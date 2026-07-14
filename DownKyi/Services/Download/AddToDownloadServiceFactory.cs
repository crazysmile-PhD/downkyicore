using System;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;

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

    public AddToDownloadServiceFactory(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService,
        ISettingsStore settingsStore)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _downloadStorageService = downloadStorageService ?? throw new ArgumentNullException(nameof(downloadStorageService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public AddToDownloadService Create(PlayStreamType streamType)
    {
        return new AddToDownloadService(streamType, _downloadLists, _downloadStorageService, _settingsStore);
    }

    public AddToDownloadService Create(string id, PlayStreamType streamType)
    {
        return new AddToDownloadService(id, streamType, _downloadLists, _downloadStorageService, _settingsStore);
    }
}
