using System;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Services.Video;
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
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly ISettingsStore _settingsStore;
    private readonly IUserNotificationService _notificationService;
    private readonly IAppDialogService _dialogService;
    private readonly ILogger<AddToDownloadService> _logger;
    private readonly IVideoTagProvider _tagProvider;

    public AddToDownloadServiceFactory(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IUserNotificationService notificationService,
        IAppDialogService dialogService,
        ILogger<AddToDownloadService> logger)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _tagProvider = tagProvider ?? throw new ArgumentNullException(nameof(tagProvider));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IAddToDownloadSession Create(PlayStreamType streamType)
    {
        return new AddToDownloadService(
            streamType,
            _downloadLists,
            _projectionStore,
            _settingsStore,
            _tagProvider,
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
            _projectionStore,
            _settingsStore,
            _tagProvider,
            _notificationService,
            _dialogService,
            _logger);
    }
}
