using System;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Services.Video;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal interface IAddToDownloadServiceFactory
{
    IAddToDownloadSession Create(PlayStreamType streamType);

}

internal sealed class AddToDownloadServiceFactory : IAddToDownloadServiceFactory
{
    private readonly DownloadTaskAdmissionService _admission;
    private readonly DownloadDuplicatePolicy _duplicatePolicy;
    private readonly DownloadMovieMetadataBuilder _metadataBuilder;
    private readonly ISettingsStore _settingsStore;
    private readonly IAppDialogService _dialogService;
    private readonly ILogger<AddToDownloadService> _logger;
    private readonly IVideoTagProvider _tagProvider;
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly IBilibiliApiClient _client;

    public AddToDownloadServiceFactory(
        DownloadTaskAdmissionService admission,
        DownloadDuplicatePolicy duplicatePolicy,
        DownloadMovieMetadataBuilder metadataBuilder,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        IBilibiliApiClient client,
        IAppDialogService dialogService,
        ILogger<AddToDownloadService> logger)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _duplicatePolicy = duplicatePolicy ?? throw new ArgumentNullException(nameof(duplicatePolicy));
        _metadataBuilder = metadataBuilder ?? throw new ArgumentNullException(nameof(metadataBuilder));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _tagProvider = tagProvider ?? throw new ArgumentNullException(nameof(tagProvider));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IAddToDownloadSession Create(PlayStreamType streamType)
    {
        return new AddToDownloadService(
            streamType,
            _admission,
            _duplicatePolicy,
            _metadataBuilder,
            _settingsStore,
            _tagProvider,
            _wbiKeyProvider,
            _client,
            _dialogService,
            _logger);
    }

}
