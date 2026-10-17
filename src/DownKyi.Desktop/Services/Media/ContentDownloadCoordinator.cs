using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Video;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Media;

internal enum DownloadInfoKind
{
    Video,
    Bangumi
}

internal sealed record ContentDownloadItem(string Source, DownloadInfoKind Kind, bool IsSelected);

internal readonly record struct ContentDownloadBatchResult(
    int AddedCount,
    int SkippedCount,
    int DuplicateCount = 0,
    int FailedCount = 0);

internal readonly record struct ContentDownloadBatchProgress(
    int ProcessedCount,
    int TotalCount,
    int AddedCount,
    int SkippedCount,
    int DuplicateCount,
    int FailedCount);

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
    Task<ContentDownloadBatchResult?> AddAsync(
        IReadOnlyList<ContentDownloadItem> items,
        bool onlySelected,
        CancellationToken cancellationToken);

    Task<ContentDownloadBatchResult?> AddIncrementallyAsync(
        IReadOnlyList<ContentDownloadItem> items,
        bool onlySelected,
        IProgress<ContentDownloadBatchProgress>? progress,
        CancellationToken cancellationToken) =>
        AddAsync(items, onlySelected, cancellationToken);
}

internal sealed class ContentDownloadCoordinator : IContentDownloadCoordinator
{
    private const int IncrementalPreparationBatchSize = 5;
    private readonly IAddToDownloadServiceFactory _serviceFactory;
    private readonly IContentInfoServiceFactory _infoServiceFactory;
    private readonly DownloadActionPlanner _actionPlanner;
    private readonly ILogger<ContentDownloadCoordinator> _logger;

    public ContentDownloadCoordinator(
        IAddToDownloadServiceFactory serviceFactory,
        IContentInfoServiceFactory infoServiceFactory,
        DownloadActionPlanner actionPlanner,
        ILogger<ContentDownloadCoordinator> logger)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        _infoServiceFactory = infoServiceFactory ?? throw new ArgumentNullException(nameof(infoServiceFactory));
        _actionPlanner = actionPlanner ?? throw new ArgumentNullException(nameof(actionPlanner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ContentDownloadBatchResult?> AddAsync(
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
            return new ContentDownloadBatchResult(AddedCount: 0, SkippedCount: 0);
        }

        var addToDownloadSession = _serviceFactory.Create(ToPlayStreamType(selectedItems[0].Kind));
        var flow = await DownloadAddCoordinator.AddToDownloadIfSelectionAcceptedAsync(
            () => addToDownloadSession.SelectDownloadAsync(cancellationToken: cancellationToken),
            selection => AddItemsAsync(
                addToDownloadSession,
                selectedItems,
                selection,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        return flow.Outcome == DownloadAddFlowOutcome.Completed
            ? flow.Value
            : null;
    }

    public async Task<ContentDownloadBatchResult?> AddIncrementallyAsync(
        IReadOnlyList<ContentDownloadItem> items,
        bool onlySelected,
        IProgress<ContentDownloadBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedItems = onlySelected
            ? items.Where(item => item.IsSelected).ToArray()
            : items.ToArray();
        if (selectedItems.Length == 0)
        {
            return new ContentDownloadBatchResult(AddedCount: 0, SkippedCount: 0);
        }

        progress?.Report(new ContentDownloadBatchProgress(
            ProcessedCount: 0,
            TotalCount: selectedItems.Length,
            AddedCount: 0,
            SkippedCount: 0,
            DuplicateCount: 0,
            FailedCount: 0));
        var addToDownloadSession = _serviceFactory.Create(ToPlayStreamType(selectedItems[0].Kind));
        var flow = await DownloadAddCoordinator.AddToDownloadIfSelectionAcceptedAsync(
            () => addToDownloadSession.SelectDownloadAsync(cancellationToken: cancellationToken),
            selection => AddItemsIncrementallyAsync(
                addToDownloadSession,
                selectedItems,
                selection,
                progress,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        return flow.Outcome == DownloadAddFlowOutcome.Completed
            ? flow.Value
            : null;
    }

    private Task<ContentDownloadBatchResult> AddItemsAsync(
        IAddToDownloadSession addToDownloadSession,
        ContentDownloadItem[] items,
        DownloadAddSelection selection,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var addedCount = 0;
            var skippedCount = 0;
            var duplicateCount = 0;
            var failedCount = 0;
            var preparedDownloads = new List<PreparedDownload>(items.Length);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PreparedDownload? preparedDownload;
                try
                {
                    var infoService = await _infoServiceFactory
                        .CreateAsync(item, cancellationToken)
                        .ConfigureAwait(false);
                    preparedDownload = await addToDownloadSession
                        .PrepareAsync(infoService, selection.RequestedContent, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (BilibiliApiResponseException exception) when (IsUnavailableVideo(exception))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    skippedCount++;
                    _logger.LogWarningMessage(
                        $"A content download item was skipped because Bilibili reported it unavailable; " +
                        $"operation={exception.Operation}; code={exception.Code}.",
                        exception);
                    continue;
                }

                if (preparedDownload == null)
                {
                    skippedCount++;
                    continue;
                }

                preparedDownloads.Add(preparedDownload);
            }

            var finalizedDownloads = await _actionPlanner
                .PlanBatchAsync(
                    selection.RequestedContent,
                    preparedDownloads,
                    isAll: false,
                    new DownloadContentConflictChoices(),
                    cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            var addResult = await addToDownloadSession
                .AddToDownloadBatch(selection, finalizedDownloads, cancellationToken)
                .ConfigureAwait(false);
            addedCount += addResult.AddedCount;
            duplicateCount += addResult.DuplicateCount;
            failedCount += addResult.FailedCount;
            skippedCount += addResult.SkippedCount;

            return new ContentDownloadBatchResult(
                addedCount,
                skippedCount,
                duplicateCount,
                failedCount);
        }, cancellationToken);
    }

    private Task<ContentDownloadBatchResult> AddItemsIncrementallyAsync(
        IAddToDownloadSession addToDownloadSession,
        ContentDownloadItem[] items,
        DownloadAddSelection selection,
        IProgress<ContentDownloadBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var processedCount = 0;
            var addedCount = 0;
            var skippedCount = 0;
            var duplicateCount = 0;
            var failedCount = 0;
            var conflictChoices = new DownloadContentConflictChoices(
                qualitySubstitutionDecisionAppliesToRemainingItems: true);

            foreach (var itemBatch in items.Chunk(IncrementalPreparationBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preparedDownloads = new List<PreparedDownload>(itemBatch.Length);
                foreach (var item in itemBatch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PreparedDownload? preparedDownload;
                    try
                    {
                        var infoService = await _infoServiceFactory
                            .CreateAsync(item, cancellationToken)
                            .ConfigureAwait(false);
                        preparedDownload = await addToDownloadSession
                            .PrepareAsync(infoService, selection.RequestedContent, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (BilibiliApiResponseException exception) when (IsUnavailableVideo(exception))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        skippedCount++;
                        processedCount++;
                        ReportProgress();
                        _logger.LogWarningMessage(
                            $"A content download item was skipped because Bilibili reported it unavailable; " +
                            $"operation={exception.Operation}; code={exception.Code}.",
                            exception);
                        continue;
                    }

                    if (preparedDownload == null)
                    {
                        skippedCount++;
                        processedCount++;
                        ReportProgress();
                        continue;
                    }

                    preparedDownloads.Add(preparedDownload);
                }

                var finalizedDownloads = await _actionPlanner
                    .PlanBatchAsync(
                        selection.RequestedContent,
                        preparedDownloads,
                        isAll: false,
                        conflictChoices,
                        cancellationToken)
                    .ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                var addResult = await addToDownloadSession
                    .AddToDownloadBatch(selection, finalizedDownloads, cancellationToken)
                    .ConfigureAwait(false);
                addedCount += addResult.AddedCount;
                duplicateCount += addResult.DuplicateCount;
                failedCount += addResult.FailedCount;
                skippedCount += addResult.SkippedCount;
                processedCount += preparedDownloads.Count;
                ReportProgress();
            }

            return new ContentDownloadBatchResult(
                addedCount,
                skippedCount,
                duplicateCount,
                failedCount);

            void ReportProgress()
            {
                progress?.Report(new ContentDownloadBatchProgress(
                    processedCount,
                    items.Length,
                    addedCount,
                    skippedCount,
                    duplicateCount,
                    failedCount));
            }
        }, cancellationToken);
    }

    private static bool IsUnavailableVideo(BilibiliApiResponseException exception) =>
        exception.Code == 62002
        && string.Equals(
            exception.Operation,
            nameof(VideoInfo.VideoViewInfoAsync),
            StringComparison.Ordinal);

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
