using System;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal interface IAddToDownloadServiceFactory
{
    IAddToDownloadSession Create(PlayStreamType streamType);

    IAddToDownloadSession Create(string id, PlayStreamType streamType);
}

internal sealed class AddToDownloadServiceFactory : IAddToDownloadServiceFactory
{
    private readonly DownloadListState _downloadLists;
    private readonly DownloadStorageService _downloadStorageService;
    private readonly ISettingsStore _settingsStore;
    private readonly IUserNotificationService _notificationService;
    private readonly IAppDialogService _dialogService;
    private readonly ILogger<AddToDownloadService> _logger;

    public AddToDownloadServiceFactory(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService,
        ISettingsStore settingsStore,
        IUserNotificationService notificationService,
        IAppDialogService dialogService,
        ILogger<AddToDownloadService> logger)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _downloadStorageService = downloadStorageService ?? throw new ArgumentNullException(nameof(downloadStorageService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IAddToDownloadSession Create(PlayStreamType streamType)
    {
        return new AddToDownloadService(
            streamType,
            _downloadLists,
            _downloadStorageService,
            _settingsStore,
            _notificationService,
            _dialogService,
            _logger);
    }

    public IAddToDownloadSession Create(string id, PlayStreamType streamType)
    {
        return new AddToDownloadService(
            id,
            streamType,
            _downloadLists,
            _downloadStorageService,
            _settingsStore,
            _notificationService,
            _dialogService,
            _logger);
    }
}
