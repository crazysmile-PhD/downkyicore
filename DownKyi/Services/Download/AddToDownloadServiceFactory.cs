using System;
using DownKyi.Core.BiliApi.VideoStream;

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

    public AddToDownloadServiceFactory(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _downloadStorageService = downloadStorageService ?? throw new ArgumentNullException(nameof(downloadStorageService));
    }

    public AddToDownloadService Create(PlayStreamType streamType)
    {
        return new AddToDownloadService(streamType, _downloadLists, _downloadStorageService);
    }

    public AddToDownloadService Create(string id, PlayStreamType streamType)
    {
        return new AddToDownloadService(id, streamType, _downloadLists, _downloadStorageService);
    }
}
