using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DownKyi.Domain.Downloads;
using DownKyi.Images;
using DownKyi.Models;
using DownKyi.Services.Download;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadPauseStatePresentationTests
{
    [AvaloniaFact]
    public async Task WaitingPausingAndPausedRemainDistinctInTheDownloadList()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var queued = CreateTask();
            var pausing = queued
                .Start(DateTimeOffset.UnixEpoch.AddSeconds(1))
                .RequireValue()
                .Pause(DateTimeOffset.UnixEpoch.AddSeconds(2))
                .RequireValue();
            var paused = pausing
                .ConfirmPaused(DateTimeOffset.UnixEpoch.AddSeconds(3))
                .RequireValue();

            var queuedItem = DownloadTaskProjectionMapper.ToDownloadingItem(queued);
            var pausingItem = DownloadTaskProjectionMapper.ToDownloadingItem(pausing);
            var pausedItem = DownloadTaskProjectionMapper.ToDownloadingItem(paused);

            Assert.Equal(DownloadStatus.WaitForDownload, queuedItem.Downloading.DownloadStatus);
            Assert.Equal("等待中……", queuedItem.DownloadStatusTitle);
            Assert.Same(ButtonIcon.Pause, queuedItem.StartOrPause);

            Assert.Equal(DownloadStatus.PauseStarted, pausingItem.Downloading.DownloadStatus);
            Assert.Equal("暂停中……", pausingItem.DownloadStatusTitle);
            Assert.Same(ButtonIcon.Start, pausingItem.StartOrPause);

            Assert.Equal(DownloadStatus.Pause, pausedItem.Downloading.DownloadStatus);
            Assert.Equal("已暂停", pausedItem.DownloadStatusTitle);
            Assert.Same(ButtonIcon.Start, pausedItem.StartOrPause);
        }).ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task RetiredLegacyFailureHidesTheUnavailableRetryAction()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var queued = CreateTask();
            var failed = DownloadTask.Restore(
                queued.Id,
                queued.Metadata,
                queued.Plan,
                queued.Output,
                DownloadPhase.Failed,
                DownloadProgress.None,
                DownloadTransferState.LegacyEmpty,
                new DownloadFailure(
                    "download.transfer.legacy-backend-removed",
                    "The unfinished legacy download was removed and marked as failed.",
                    false),
                completion: null,
                version: 1,
                queued.CreatedAtUtc,
                queued.CreatedAtUtc.AddSeconds(1));

            var item = DownloadTaskProjectionMapper.ToDownloadingItem(failed);

            Assert.Equal(DownloadStatus.DownloadFailed, item.Downloading.DownloadStatus);
            Assert.Same(ButtonIcon.Retry, item.StartOrPause);
            Assert.False(item.IsToggleVisible);
        }).ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task FailedTaskKeepsDetailsAfterProjectionAndClearsThemOnRetry()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var failed = CreateTask()
                .Fail(
                    new DownloadFailure("download.transfer.http-403", "下载失败", true),
                    DateTimeOffset.UnixEpoch.AddSeconds(1))
                .RequireValue();
            var item = DownloadTaskProjectionMapper.ToDownloadingItem(failed);

            Assert.True(item.HasFailureDetails);
            Assert.Equal("download.transfer.http-403", item.Failure?.Code);
            item.IsFailureDetailsExpanded = true;
            Assert.True(item.IsFailureDetailsExpanded);

            DownloadTaskProjectionMapper.Apply(
                failed.Retry(DateTimeOffset.UnixEpoch.AddSeconds(2)).RequireValue(),
                item);

            Assert.False(item.HasFailureDetails);
            Assert.False(item.IsFailureDetailsExpanded);
            Assert.Null(item.Failure);
        }).ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task NonTransientFailureHidesRetryEvenWithoutLegacyProvenance()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var failed = CreateTask()
                .Fail(
                    new DownloadFailure(
                        "download.publish.reconcile",
                        "Pending publication could not be safely reconciled.",
                        false),
                    DateTimeOffset.UnixEpoch.AddSeconds(1))
                .RequireValue();

            var item = DownloadTaskProjectionMapper.ToDownloadingItem(failed);

            Assert.False(item.IsToggleVisible);
        }).ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task RepeatedStateRefreshReusesResourcesUntilTheirInputsChange()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureDownloadProjectionResources();
            var resources = new CountingProjectionResources();
            application.Resources.MergedDictionaries.Add(resources);
            try
            {
                var started = CreateTask().Start(DateTimeOffset.UnixEpoch.AddSeconds(1)).RequireValue();
                var item = DownloadTaskProjectionMapper.ToDownloadingItem(started);
                var zoneImage = item.ZoneImage;
                var initialZoneLookups = resources.ZoneLookups;
                var initialOperationLookups = resources.OperationLookups;
                var progressed = started.UpdateProgressAndTransfer(
                    new DownloadProgress(50, 500, 1000), started.Transfer,
                    DateTimeOffset.UnixEpoch.AddSeconds(2)).RequireValue();
                var resized = progressed.UpdateOutput(
                    new DownloadOutput(progressed.Output.BasePath, "1 KB"),
                    DateTimeOffset.UnixEpoch.AddSeconds(3)).RequireValue();

                DownloadTaskProjectionMapper.Apply(resized, item);

                Assert.Equal(50, item.Progress);
                Assert.Equal("1 KB", item.FileSize);
                Assert.Same(zoneImage, item.ZoneImage);
                Assert.Equal(initialZoneLookups, resources.ZoneLookups);
                Assert.Equal(initialOperationLookups, resources.OperationLookups);
                var pausing = resized.Pause(DateTimeOffset.UnixEpoch.AddSeconds(4)).RequireValue();
                DownloadTaskProjectionMapper.Apply(pausing, item);
                DownloadTaskProjectionMapper.Apply(
                    pausing.ConfirmPaused(DateTimeOffset.UnixEpoch.AddSeconds(5)).RequireValue(), item);

                Assert.Same(ButtonIcon.Start, item.StartOrPause);
                Assert.Equal("StartDownload", item.OperationTip);
                Assert.Equal(initialOperationLookups + 1, resources.OperationLookups);
                item.DownloadBase = new DownloadBase { Id = started.Id.Value, ZoneId = 1 };
                Assert.NotSame(zoneImage, item.ZoneImage);
                Assert.Equal(initialZoneLookups + 1, resources.ZoneLookups);
            }
            finally
            {
                application.Resources.MergedDictionaries.Remove(resources);
            }
        }).ConfigureAwait(true);
    }

    private sealed class CountingProjectionResources : ResourceProvider
    {
        public int ZoneLookups { get; private set; }
        public int OperationLookups { get; private set; }
        public override bool HasResources => true;

        public override bool TryGetResource(object key, ThemeVariant? theme, out object? value)
        {
            if (key is string text && text.EndsWith("DrawingImage", StringComparison.Ordinal))
            {
                ZoneLookups++;
                value = new DrawingImage();
                return true;
            }

            if (key is "PauseDownload" or "StartDownload" or "RetryDownload")
            {
                OperationLookups++;
                value = key;
                return true;
            }

            value = null;
            return false;
        }
    }

    private static DownloadTask CreateTask()
    {
        return DownloadTask.Create(
            new DownloadTaskId("pause-presentation"),
            new DownloadTaskMetadata(
                new DownloadMediaIdentity("BV1", 1, 1, 0, 1, 1),
                "title",
                "part",
                "00:01",
                "avc1",
                new DownloadQuality(80, "1080P"),
                new DownloadQuality(30280, "AAC"),
                string.Empty,
                string.Empty,
                0),
            new DownloadPlan(DownloadContentSelection.None, [], 0, nfoRequest: null),
            new DownloadOutput("pause-presentation", null),
            DateTimeOffset.UnixEpoch);
    }
}
