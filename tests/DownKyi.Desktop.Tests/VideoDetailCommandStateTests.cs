using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Application.Media;
using DownKyi.Commands;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.ViewModels;
using DownKyi.ViewModels.UiState;
using DownKyi.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class VideoDetailCommandStateTests
{
    [AvaloniaFact]
    public void ResizingKeepsVideoSelectionAndActionsAccessible()
    {
        var application = DesktopTestResources.EnsureProductThemeResources();
        var fluentTheme = new FluentTheme();
        var gridStyles = new StyleInclude(new Uri("avares://DownKyi.Desktop.Tests/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        };
        application.Styles.Insert(0, fluentTheme);
        application.Styles.Insert(1, gridStyles);
        using var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), $"video-resize-{Guid.NewGuid():N}.json"));
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        using var viewModel = new ViewVideoDetailViewModel(
            new DesktopInteractionContextStub(), new ClipboardServiceStub(), settings, workflow,
            new VideoDetailDownloadCoordinatorStub(), NullLogger<ViewVideoDetailViewModel>.Instance);
        viewModel.UiState.VideoInfoView = new VideoInfoView
        {
            Title = string.Concat(Enumerable.Repeat("很长的影片标题与布局检查", 8)),
            Description = string.Join(Environment.NewLine, Enumerable.Repeat("长简介仍然可以滚动阅读。", 40)),
            UpName = "作者名称",
            VideoZone = "知识 · 设计",
            CreateTime = "2026-10-23"
        };
        var pages = Enumerable.Range(1, 80).Select(i => new VideoPage { Order = i, Cid = i, Name = $"影片 {i}", Duration = "24:30" }).ToList();
        viewModel.VideoSections.Add(new VideoSection { Title = "第一季", IsSelected = true, VideoPages = pages });
        viewModel.VideoSections.Add(new VideoSection { Title = "第二季", VideoPages = [] });
        viewModel.UiState.DisplayState = VideoDetailDisplayState.Content;
        var view = new ViewVideoDetail { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1440, Height = 900 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var input = view.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "DetailInput");
            input.Text = "BV17x411w7KC";
            Assert.True(input.Focus());
            var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            grid.SelectedItem = pages[2];
            foreach (var size in new[] { new Size(798, 518), new Size(800, 550), new Size(960, 1000), new Size(1179, 700), new Size(1180, 700), new Size(1920, 1080), new Size(800, 550) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(input.IsFocused);
                Assert.Equal("BV17x411w7KC", viewModel.UiState.InputText);
                Assert.True(pages[2].IsSelected);
                Assert.True(viewModel.VideoSections[0].IsSelected);
                Assert.True(grid.Bounds.Height >= 130);
                Assert.True(grid.Bounds.Width >= 500);
                Assert.True(input.Bounds.Width >= 250);
                var summary = view.FindControl<VideoDetailSummaryView>("DetailSummary")!;
                var workspace = view.FindControl<Grid>("DetailWorkspace")!;
                Assert.True(summary.Bounds.Right <= workspace.Bounds.Left || summary.Bounds.Bottom <= workspace.Bounds.Top);
                foreach (var control in view.GetVisualDescendants().OfType<Control>().Where(c => c is Button or TextBox))
                {
                    var point = control.TranslatePoint(default, view)!.Value;
                    Assert.InRange(point.X, -0.5, view.Bounds.Width - control.Bounds.Width + 0.5);
                }
                var actions = view.FindControl<VideoDetailActionsView>("DetailActions")!;
                var bottom = actions.TranslatePoint(new Point(0, actions.Bounds.Height), view)!.Value.Y;
                Assert.True(bottom <= view.Bounds.Height);
            }
            viewModel.UiState.DisplayState = VideoDetailDisplayState.Busy;
            window.UpdateLayout();
            var parse = view.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "DetailParseButton");
            Assert.False(parse.IsEffectivelyEnabled);
            viewModel.UiState.DisplayState = VideoDetailDisplayState.Content;
            window.UpdateLayout();
            Assert.True(parse.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
            application.Styles.Remove(gridStyles);
            application.Styles.Remove(fluentTheme);
        }
    }

    [AvaloniaFact]
    public void BusyRecoveryInvalidatesEveryBusyDependentCommand()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-command-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        using var viewModel = new ViewVideoDetailViewModel(
            new DesktopInteractionContextStub(),
            new ClipboardServiceStub(),
            settings,
            workflow,
            new VideoDetailDownloadCoordinatorStub(),
            NullLogger<ViewVideoDetailViewModel>.Instance);
        var commands = new[]
        {
            viewModel.InputCommand,
            viewModel.AddToDownloadCommand
        };
        var invalidationCounts = commands.ToDictionary(command => command, _ => 0);
        foreach (var command in commands)
        {
            command.CanExecuteChanged += (_, _) => invalidationCounts[command]++;
        }

        foreach (var recoveryState in new[]
        {
            VideoDetailDisplayState.Content,
            VideoDetailDisplayState.Idle,
            VideoDetailDisplayState.Empty
        })
        {
            viewModel.UiState.DisplayState = VideoDetailDisplayState.Busy;

            Assert.All(commands, command => Assert.False(command.CanExecute(null)));

            viewModel.UiState.DisplayState = recoveryState;

            Assert.False(viewModel.UiState.IsBusy);
            Assert.All(commands, command => Assert.True(command.CanExecute(null)));
        }

        Assert.All(commands, command => Assert.Equal(6, invalidationCounts[command]));
    }

    [AvaloniaFact]
    public void BusyToContentUpdatesAvaloniaActionButtons()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-buttons-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        using var viewModel = new ViewVideoDetailViewModel(
            new DesktopInteractionContextStub(),
            new ClipboardServiceStub(),
            settings,
            workflow,
            new VideoDetailDownloadCoordinatorStub(),
            NullLogger<ViewVideoDetailViewModel>.Instance);
        var view = new VideoDetailActionsView { DataContext = viewModel };
        var window = new Window { Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            var buttons = view
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, viewModel.AddToDownloadCommand))
                .ToArray();
            Assert.Single(buttons);

            viewModel.UiState.DisplayState = VideoDetailDisplayState.Busy;
            window.UpdateLayout();
            Assert.All(buttons, button => Assert.False(button.IsEffectivelyEnabled));

            viewModel.UiState.DisplayState = VideoDetailDisplayState.Content;
            window.UpdateLayout();
            Assert.All(buttons, button => Assert.True(button.IsEffectivelyEnabled));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task NavigatingToDownloadsWhileAddIsPendingKeepsTheOperationAlive()
    {
        EnsureProductLanguageResources();
        using var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), $"download-navigation-{Guid.NewGuid():N}.json"));
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        var coordinator = new VideoDetailDownloadCoordinatorStub { WaitForRelease = true };
        ViewVideoDetailViewModel? viewModel = null;
        using var navigation = new AvaloniaNavigationService(
            route => route == AppRoute.VideoDetail ? viewModel! : new object(), action => action());
        var interactions = new DesktopInteractionContextStub { Navigation = navigation };
        using var ownedViewModel = viewModel = new ViewVideoDetailViewModel(interactions,
            new ClipboardServiceStub(), settings, workflow, coordinator, NullLogger<ViewVideoDetailViewModel>.Instance);
        navigation.Navigate(new AppNavigationRequest(AppRoute.VideoDetail));
        viewModel.UiState.VideoInfoView = new VideoInfoView();
        var completion = WaitUntilExecutableAfterDisabled(viewModel.AddToDownloadCommand);
        viewModel.AddToDownloadCommand.Execute(null);
        await coordinator.AddRequested.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        viewModel.DownloadManagerCommand.Execute(null);
        Assert.NotSame(viewModel, navigation.GetActiveView(AppNavigationRegion.Main));
        Assert.True(navigation.CanGoBack(AppNavigationRegion.Main));
        coordinator.ReleaseAdd();
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(VideoDetailOperationStatus.Success, viewModel.UiState.OperationStatus);
        Assert.Equal(1, coordinator.AddRequestCount);
    }

    [AvaloniaFact]
    public async Task CanceledDownloadSelectionIsNotReportedAsAServiceFailure()
    {
        EnsureProductLanguageResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-canceled-add-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        var interactions = new DesktopInteractionContextStub();
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub
        {
            Result = DownloadAddResult.Stopped(
                DownloadAddOutcome.Canceled,
                DownloadAddStopReason.SelectionCanceled)
        };
        using var viewModel = new ViewVideoDetailViewModel(
            interactions,
            new ClipboardServiceStub(),
            settings,
            workflow,
            downloadCoordinator,
            NullLogger<ViewVideoDetailViewModel>.Instance);
        viewModel.UiState.VideoInfoView = new DownKyi.Presentation.VideoInfoView();
        var completion = WaitUntilExecutableAfterDisabled(viewModel.AddToDownloadCommand);

        viewModel.AddToDownloadCommand.Execute(null);
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(VideoDetailOperationStatus.Canceled, viewModel.UiState.OperationStatus);
        Assert.Equal("已取消新增下载任务。", viewModel.UiState.OperationStatusText);
        Assert.Equal(["已取消新增下载任务。"], interactions.RecordedNotifications.Messages);
    }

    [AvaloniaFact]
    public async Task DownloadFailureSettlesRunningStatus()
    {
        EnsureProductLanguageResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-failed-add-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub
        {
            Exception = new HttpRequestException("prepare failed")
        };
        using var viewModel = new ViewVideoDetailViewModel(
            new DesktopInteractionContextStub(),
            new ClipboardServiceStub(),
            settings,
            workflow,
            downloadCoordinator,
            NullLogger<ViewVideoDetailViewModel>.Instance);
        viewModel.UiState.VideoInfoView = new DownKyi.Presentation.VideoInfoView();
        var completion = WaitUntilExecutableAfterDisabled(viewModel.AddToDownloadCommand);

        viewModel.AddToDownloadCommand.Execute(null);
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(VideoDetailOperationStatus.Error, viewModel.UiState.OperationStatus);
        Assert.Equal("prepare failed", viewModel.UiState.OperationStatusText);
        Assert.NotEqual(VideoDetailOperationStatus.Running, viewModel.UiState.OperationStatus);
    }

    [AvaloniaFact]
    public async Task UnrelatedCancellationIsNotReportedAsTheCurrentOperationBeingCanceled()
    {
        EnsureProductLanguageResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-unrelated-cancel-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        using var unrelatedCancellation = new CancellationTokenSource();
        await unrelatedCancellation.CancelAsync().ConfigureAwait(true);
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub
        {
            Exception = new OperationCanceledException(unrelatedCancellation.Token)
        };
        using var viewModel = new ViewVideoDetailViewModel(
            new DesktopInteractionContextStub(),
            new ClipboardServiceStub(),
            settings,
            workflow,
            downloadCoordinator,
            NullLogger<ViewVideoDetailViewModel>.Instance);
        viewModel.UiState.VideoInfoView = new DownKyi.Presentation.VideoInfoView();
        var completion = WaitUntilExecutableAfterDisabled(viewModel.AddToDownloadCommand);

        viewModel.AddToDownloadCommand.Execute(null);
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(VideoDetailOperationStatus.Error, viewModel.UiState.OperationStatus);
        Assert.Equal("操作失败，请查看日志。", viewModel.UiState.OperationStatusText);
    }

    [AvaloniaFact]
    public async Task CancellationAfterSubmissionKeepsTheRecordedCounts()
    {
        EnsureProductLanguageResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-partial-cancel-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        var interactions = new DesktopInteractionContextStub();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync().ConfigureAwait(true);
        var result = DownloadAddResult.CanceledWithProgress(1, 0, 0, 0);
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub
        {
            Exception = new DownloadAddCanceledException(
                result,
                new OperationCanceledException(canceled.Token),
                canceled.Token)
        };
        using var viewModel = new ViewVideoDetailViewModel(
            interactions,
            new ClipboardServiceStub(),
            settings,
            workflow,
            downloadCoordinator,
            NullLogger<ViewVideoDetailViewModel>.Instance);
        viewModel.UiState.VideoInfoView = new DownKyi.Presentation.VideoInfoView();
        var completion = WaitUntilExecutableAfterDisabled(viewModel.AddToDownloadCommand);

        viewModel.AddToDownloadCommand.Execute(null);
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(VideoDetailOperationStatus.Canceled, viewModel.UiState.OperationStatus);
        Assert.Contains("成功添加了1项", viewModel.UiState.OperationStatusText, StringComparison.Ordinal);
        Assert.Contains("操作随后取消", viewModel.UiState.OperationStatusText, StringComparison.Ordinal);
        Assert.Equal([viewModel.UiState.OperationStatusText], interactions.RecordedNotifications.Messages);
    }

    [AvaloniaFact]
    public async Task DuplicateOwnerNotificationIsNotOverwrittenWhenNothingNewIsAdded()
    {
        EnsureProductLanguageResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-duplicate-message-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        var interactions = new DesktopInteractionContextStub();
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub
        {
            Result = DownloadAddResult.FromCounts(
                addedCount: 0,
                duplicateCount: 1,
                failedCount: 0,
                skippedCount: 0),
            WaitForRelease = true
        };
        using var viewModel = new ViewVideoDetailViewModel(
            interactions,
            new ClipboardServiceStub(),
            settings,
            workflow,
            downloadCoordinator,
            NullLogger<ViewVideoDetailViewModel>.Instance);
        viewModel.UiState.VideoInfoView = new DownKyi.Presentation.VideoInfoView();
        viewModel.VideoSections.Add(new DownKyi.Presentation.VideoSection
        {
            VideoPages =
            [
                new DownKyi.Presentation.VideoPage
                {
                    IsSelected = true,
                    PlaybackAvailability = new DownKyi.Core.BiliApi.VideoStream.Models.PlayUrlAvailability(
                    [
                        new DownKyi.Core.BiliApi.VideoStream.Models.PlayUrlVideoAvailability(
                            80,
                            7,
                            DownKyi.Core.BiliApi.VideoStream.Models.PlayUrlStreamKind.Dash,
                            "1080P")
                    ],
                    [])
                }
            ]
        });
        var completion = WaitUntilExecutable(viewModel.AddToDownloadCommand);

        viewModel.AddToDownloadCommand.Execute(null);
        await downloadCoordinator.AddRequested.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        downloadCoordinator.ReleaseAdd();
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(
            ["此内容已加入下载队列，无需重复添加。"],
            interactions.RecordedNotifications.Messages);
        Assert.Equal(1, downloadCoordinator.AddRequestCount);
    }

    [AvaloniaFact]
    public async Task MissingDownloadSelectionStillExplainsWhyNothingWasAdded()
    {
        EnsureProductLanguageResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-empty-selection-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        var interactions = new DesktopInteractionContextStub();
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub
        {
            Result = DownloadAddResult.Stopped(
                DownloadAddOutcome.Rejected,
                DownloadAddStopReason.NoPagesSelected)
        };
        using var viewModel = new ViewVideoDetailViewModel(
            interactions,
            new ClipboardServiceStub(),
            settings,
            workflow,
            downloadCoordinator,
            NullLogger<ViewVideoDetailViewModel>.Instance);
        viewModel.UiState.VideoInfoView = new DownKyi.Presentation.VideoInfoView();
        viewModel.VideoSections.Add(new DownKyi.Presentation.VideoSection
        {
            VideoPages = [new DownKyi.Presentation.VideoPage { IsSelected = false }]
        });
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        interactions.RecordedNotifications.NotificationRaised += (_, _) => notification.TrySetResult();

        viewModel.AddToDownloadCommand.Execute(null);
        await notification.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(["没有选中项符合下载要求！"], interactions.RecordedNotifications.Messages);
        Assert.Equal(1, downloadCoordinator.AddRequestCount);
    }

    [AvaloniaFact]
    public async Task LegacyParsingSettingsDoNotStartParsingOrDownloading()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-legacy-parsing-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            settingsPath,
            """{"SchemaVersion":1,"Basic":{"IsAutoParseVideo":2,"IsAutoDownloadAll":2,"ParseScope":2}}""",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var settings = new SettingsStore(settingsPath);
        var section = new DownKyi.Presentation.VideoSection
        {
            IsSelected = true,
            VideoPages =
            [
                new DownKyi.Presentation.VideoPage
                {
                    Bvid = "BV1G1421D7mL",
                    IsSelected = true
                }
            ]
        };
        using var workflow = new VideoDetailWorkflowCoordinatorStub
        {
            DetailResult = VideoDetailParseResult.From(
                new DownKyi.Presentation.VideoInfoView(),
                new[] { section })
        };
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub();
        using var viewModel = new ViewVideoDetailViewModel(
            new DesktopInteractionContextStub(),
            new ClipboardServiceStub(),
            settings,
            workflow,
            downloadCoordinator,
            NullLogger<ViewVideoDetailViewModel>.Instance);
        var completion = WaitUntilExecutableAfterDisabled(viewModel.InputCommand);

        viewModel.OnNavigatedTo(new AppNavigationContext(
            AppNavigationRegion.Main,
            AppRoute.VideoDetail,
            AppRoute.Index,
            new VideoNavigationPayload(new BilibiliBvidInput("BV1G1421D7mL")),
            new AppNavigationParameters()));
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(VideoDetailDisplayState.Content, viewModel.UiState.DisplayState);
        Assert.False(viewModel.UiState.IsBusy);
        Assert.True(viewModel.InputCommand.CanExecute(null));
        Assert.True(viewModel.AddToDownloadCommand.CanExecute(null));
        Assert.Equal(1, workflow.OperationStartCount);
        Assert.Equal(0, downloadCoordinator.AddRequestCount);
        Assert.False(downloadCoordinator.AddRequested.Task.IsCompleted);

        viewModel.AddToDownloadCommand.Execute(null);
        await downloadCoordinator.AddRequested.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, downloadCoordinator.AddRequestCount);
        Assert.False(downloadCoordinator.LastIsAll);
    }

    [AvaloniaFact]
    public Task FailedInputLeavesBusyAndReEnablesCommands()
    {
        return AssertInputRecoveryAsync(
            new InvalidOperationException("detail failed"),
            VideoDetailDisplayState.Empty);
    }

    [AvaloniaFact]
    public Task CanceledInputLeavesBusyAndReEnablesCommands()
    {
        return AssertInputRecoveryAsync(
            new OperationCanceledException(),
            VideoDetailDisplayState.Idle);
    }

    private static async Task AssertInputRecoveryAsync(
        Exception operationException,
        VideoDetailDisplayState expectedState)
    {
        EnsureProductLanguageResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-recovery-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub
        {
            DetailException = operationException,
            CancelCurrentOperationBeforeDetailFailure =
                operationException is OperationCanceledException
        };
        using var viewModel = new ViewVideoDetailViewModel(
            new DesktopInteractionContextStub(),
            new ClipboardServiceStub(),
            settings,
            workflow,
            new VideoDetailDownloadCoordinatorStub(),
            NullLogger<ViewVideoDetailViewModel>.Instance);
        var completion = WaitUntilExecutableAfterDisabled(viewModel.InputCommand);
        viewModel.UiState.InputText = "https://www.bilibili.com/video/BV1G1421D7mL";

        viewModel.InputCommand.Execute(null);
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(expectedState, viewModel.UiState.DisplayState);
        Assert.False(viewModel.UiState.IsBusy);
        Assert.True(viewModel.InputCommand.CanExecute(null));
        Assert.True(viewModel.AddToDownloadCommand.CanExecute(null));
        if (operationException is OperationCanceledException)
        {
            Assert.Equal(VideoDetailOperationStatus.Canceled, viewModel.UiState.OperationStatus);
            Assert.Equal("操作已取消。", viewModel.UiState.OperationStatusText);
        }
        else
        {
            Assert.Equal(VideoDetailOperationStatus.Error, viewModel.UiState.OperationStatus);
            Assert.Equal(operationException.Message, viewModel.UiState.OperationStatusText);
        }
    }

    private static void EnsureProductLanguageResources()
    {
        var application = DesktopTestResources.EnsureProductThemeResources();
        if (!application.TryGetResource(
                "TipAlreadyToAddDownloading",
                Avalonia.Styling.ThemeVariant.Default,
                out _))
        {
            application.Resources.MergedDictionaries.Add(
                new Avalonia.Markup.Xaml.Styling.ResourceInclude(
                    new Uri("avares://DownKyi.Desktop.Tests/"))
                {
                    Source = new Uri("avares://DownKyi.Desktop/Languages/zh-CN.axaml")
                });
        }
    }

    private static Task WaitUntilExecutable(DownKyiAsyncDelegateCommand command)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!command.CanExecute(null))
            {
                return;
            }

            command.CanExecuteChanged -= handler;
            completion.TrySetResult();
        };
        command.CanExecuteChanged += handler;
        return completion.Task;
    }

    private static Task WaitUntilExecutableAfterDisabled(DownKyiAsyncDelegateCommand command)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disabledObserved = false;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!command.CanExecute(null))
            {
                disabledObserved = true;
                return;
            }

            if (!disabledObserved)
            {
                return;
            }

            command.CanExecuteChanged -= handler;
            completion.TrySetResult();
        };
        command.CanExecuteChanged += handler;
        return completion.Task;
    }

    private sealed class DesktopInteractionContextStub : IDesktopInteractionContext
    {
        public DesktopInteractionContextStub(IAppDialogService? dialogs = null)
        {
            Dialogs = dialogs ?? new DialogServiceStub();
        }

        public NotificationServiceStub RecordedNotifications { get; } = new();

        public IUserNotificationService Notifications => RecordedNotifications;

        public IAppNavigationService Navigation { get; init; } = new NavigationServiceStub();

        public IAppDialogService Dialogs { get; }
    }

    private sealed class ClipboardServiceStub : IClipboardService
    {
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class VideoDetailWorkflowCoordinatorStub : IVideoDetailWorkflowCoordinator
    {
        private readonly CancellationTokenSource _operationCancellation = new();
        private int _version;

        public BilibiliMediaInput? CurrentInput { get; private set; } =
            new BilibiliBvidInput("BV17x411w7KC");

        public VideoDetailParseResult DetailResult { get; init; } = VideoDetailParseResult.Empty;

        public Exception? DetailException { get; init; }

        public bool CancelCurrentOperationBeforeDetailFailure { get; init; }

        public int OperationStartCount => Volatile.Read(ref _version);

        public VideoDetailOperation StartOperation()
        {
            return new VideoDetailOperation(
                Interlocked.Increment(ref _version),
                _operationCancellation.Token);
        }

        public bool IsCurrent(VideoDetailOperation operation)
        {
            return operation.Version == Volatile.Read(ref _version);
        }

        public void Cancel()
        {
            _operationCancellation.Cancel();
        }

        public void Reset()
        {
        }

        public void SetInput(BilibiliMediaInput input)
        {
            CurrentInput = input;
        }

        public void ApplySearch(string? searchText)
        {
        }

        public async Task<VideoDetailParseResult> LoadDetailAsync(VideoDetailOperation operation)
        {
            if (DetailException != null)
            {
                if (CancelCurrentOperationBeforeDetailFailure)
                {
                    await _operationCancellation.CancelAsync().ConfigureAwait(true);
                    throw new OperationCanceledException(operation.CancellationToken);
                }

                throw DetailException;
            }

            return DetailResult;
        }

        public void Dispose()
        {
            _operationCancellation.Dispose();
        }
    }

    private sealed class VideoDetailDownloadCoordinatorStub : IVideoDetailDownloadCoordinator
    {
        private readonly TaskCompletionSource _releaseAdd =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AddRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AddRequestCount { get; private set; }

        public bool WaitForRelease { get; init; }

        public bool LastIsAll { get; private set; }

        public DownKyi.Presentation.VideoInfoView? LastVideoInfo { get; private set; }

        public DownloadAddResult Result { get; init; } = DownloadAddResult.FromCounts(
            addedCount: 1,
            duplicateCount: 0,
            failedCount: 0,
            skippedCount: 0);

        public Exception? Exception { get; init; }

        public Action? BeforeReturn { get; init; }

        public async Task<DownloadAddResult> AddAsync(
            BilibiliMediaInput input,
            DownKyi.Presentation.VideoInfoView videoInfoView,
            IList<DownKyi.Presentation.VideoSection> videoSections,
            bool isAll,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddRequestCount++;
            LastIsAll = isAll;
            LastVideoInfo = videoInfoView;
            AddRequested.TrySetResult();
            if (WaitForRelease)
            {
                await _releaseAdd.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
            }

            BeforeReturn?.Invoke();
            if (Exception != null)
            {
                throw Exception;
            }

            return Result;
        }

        public void ReleaseAdd()
        {
            _releaseAdd.TrySetResult();
        }
    }

    private sealed class NotificationServiceStub : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

        public List<string> Messages { get; } = [];

        public void Show(string message)
        {
            Messages.Add(message);
            NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
        }
    }

    private sealed class DialogServiceStub(AppDialogResult? result = null) : IAppDialogService
    {
        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return result == null
                ? throw new NotSupportedException()
                : Task.FromResult(result);
        }
    }

    private sealed class NavigationServiceStub : IAppNavigationService
    {
        public event EventHandler<AppNavigationChangedEventArgs>? NavigationChanged
        {
            add { }
            remove { }
        }

        public void Navigate(AppNavigationRequest request)
        {
        }

        public void NavigateRegion(
            AppNavigationRegion region,
            AppRoute route,
            IReadOnlyDictionary<string, object?>? parameters = null)
        {
        }

        public void ClearRegion(AppNavigationRegion region)
        {
        }

        public bool CanGoBack(AppNavigationRegion region)
        {
            return false;
        }

        public void GoBack(AppNavigationRegion region)
        {
        }

        public object? GetActiveView(AppNavigationRegion region)
        {
            return null;
        }
    }
}
