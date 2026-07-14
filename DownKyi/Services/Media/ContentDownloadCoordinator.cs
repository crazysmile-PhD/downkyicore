using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Settings;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services.Download;
using Prism.Events;

namespace DownKyi.Services.Media;

internal enum DownloadInfoKind
{
    Video,
    Bangumi
}

internal sealed record ContentDownloadItem(string Source, DownloadInfoKind Kind, bool IsSelected);

internal interface IContentDownloadCoordinator
{
    Task<int> AddAsync(
        AddToDownloadService addToDownloadService,
        IReadOnlyList<ContentDownloadItem> items,
        bool onlySelected,
        string directory,
        IEventAggregator eventAggregator,
        IDialogService? dialogService,
        CancellationToken cancellationToken);
}

internal sealed class ContentDownloadCoordinator : IContentDownloadCoordinator
{
    private readonly ISettingsStore _settingsStore;

    public ContentDownloadCoordinator(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public Task<int> AddAsync(
        AddToDownloadService addToDownloadService,
        IReadOnlyList<ContentDownloadItem> items,
        bool onlySelected,
        string directory,
        IEventAggregator eventAggregator,
        IDialogService? dialogService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addToDownloadService);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(eventAggregator);

        return Task.Run(async () =>
        {
            var addedCount = 0;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (onlySelected && !item.IsSelected)
                {
                    continue;
                }

                IInfoService infoService = item.Kind switch
                {
                    DownloadInfoKind.Video => new VideoInfoService(item.Source, _settingsStore),
                    DownloadInfoKind.Bangumi => new BangumiInfoService(item.Source, _settingsStore),
                    _ => throw new ArgumentOutOfRangeException(nameof(items), item.Kind, null)
                };

                addToDownloadService.SetVideoInfoService(infoService);
                addToDownloadService.GetVideo();
                addToDownloadService.ParseVideo(infoService);
                cancellationToken.ThrowIfCancellationRequested();
                addedCount += await addToDownloadService
                    .AddToDownload(eventAggregator, dialogService, directory)
                    .ConfigureAwait(false);
            }

            return addedCount;
        }, cancellationToken);
    }
}
