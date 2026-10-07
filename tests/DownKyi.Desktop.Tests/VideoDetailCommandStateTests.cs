using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Services;
using DownKyi.Services.Video;
using DownKyi.ViewModels;
using DownKyi.ViewModels.UiState;
using DownKyi.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class VideoDetailCommandStateTests
{
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
            viewModel.ParseCommand,
            viewModel.ParseAllVideoCommand,
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
                .Where(button => ReferenceEquals(button.Command, viewModel.ParseAllVideoCommand)
                    || ReferenceEquals(button.Command, viewModel.AddToDownloadCommand))
                .ToArray();
            Assert.Equal(2, buttons.Length);

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
    public async Task ManualParseRecoveryEnablesAndDispatchesSelectedDownload()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-manual-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        settings.Update(current => current with
        {
            Basic = current.Basic with { ParseScope = ParseScope.All }
        });
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub();
        using var viewModel = new ViewVideoDetailViewModel(
            new DesktopInteractionContextStub(),
            new ClipboardServiceStub(),
            settings,
            workflow,
            downloadCoordinator,
            NullLogger<ViewVideoDetailViewModel>.Instance);
        var page = new DownKyi.Presentation.VideoPage
        {
            Bvid = "BV1G1421D7mL",
            IsSelected = true
        };
        var section = new DownKyi.Presentation.VideoSection
        {
            IsSelected = true,
            VideoPages = new List<DownKyi.Presentation.VideoPage> { page }
        };
        viewModel.UiState.VideoInfoView = new DownKyi.Presentation.VideoInfoView();
        viewModel.VideoSections.Add(section);
        viewModel.UiState.DisplayState = VideoDetailDisplayState.Content;

        viewModel.ParseAllVideoCommand.Execute(null);
        await workflow.PageStreamsStarted.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(viewModel.UiState.IsBusy);
        Assert.True(viewModel.UiState.IsContentVisible);
        Assert.False(viewModel.AddToDownloadCommand.CanExecute(null));
        var downloadEnabled = WaitUntilExecutable(viewModel.AddToDownloadCommand);

        workflow.ReleasePageStreams();
        await downloadEnabled.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(VideoDetailDisplayState.Content, viewModel.UiState.DisplayState);
        Assert.True(viewModel.AddToDownloadCommand.CanExecute(null));

        viewModel.AddToDownloadCommand.Execute(null);
        await downloadCoordinator.AddRequested.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.False(downloadCoordinator.LastIsAll);
        Assert.Same(viewModel.UiState.VideoInfoView, downloadCoordinator.LastVideoInfo);
    }

    [AvaloniaFact]
    public async Task ParsingSelectorAcceptedScopesDispatchExactlyTheTypedSelection()
    {
        DesktopTestResources.EnsureProductThemeResources();
        foreach (var scope in new[]
        {
            ParseScope.SelectedItem,
            ParseScope.CurrentSection,
            ParseScope.All
        })
        {
            var settingsPath = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-video-detail-selector-{Guid.NewGuid():N}.json");
            using var settings = new SettingsStore(settingsPath);
            settings.Update(current => current with
            {
                Basic = current.Basic with { ParseScope = ParseScope.None }
            });
            using var workflow = new VideoDetailWorkflowCoordinatorStub();
            var parameters = new Dictionary<string, object?>(
                ParsingSelectorDialog.Encode(new ParsingSelectorResult(scope)))
            {
                ["metadata"] = "ignored"
            };
            var dialogService = new DialogServiceStub(new AppDialogResult(
                AppDialogOutcome.Accepted,
                parameters));
            using var viewModel = new ViewVideoDetailViewModel(
                new DesktopInteractionContextStub(dialogService),
                new ClipboardServiceStub(),
                settings,
                workflow,
                new VideoDetailDownloadCoordinatorStub(),
                NullLogger<ViewVideoDetailViewModel>.Instance);
            var completion = WaitUntilExecutableAfterDisabled(viewModel.ParseAllVideoCommand);

            viewModel.ParseAllVideoCommand.Execute(null);
            await workflow.PageStreamsStarted.Task
                .WaitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(scope, workflow.LastParseScope);

            workflow.ReleasePageStreams();
            await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
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
            Result = 0,
            WaitForRelease = true,
            BeforeReturn = () => interactions.RecordedNotifications.Show(
                DownKyi.Utils.DictionaryResource.GetString("TipAlreadyToAddDownloading"))
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
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub { Result = 0 };
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
    public async Task ParsingSelectorInvalidAcceptedResultFailsBeforeDispatch()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-selector-invalid-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        settings.Update(current => current with
        {
            Basic = current.Basic with { ParseScope = ParseScope.None }
        });
        using var workflow = new VideoDetailWorkflowCoordinatorStub();
        var dialogService = new DialogServiceStub(new AppDialogResult(
            AppDialogOutcome.Accepted,
            new Dictionary<string, object?>
            {
                ["metadata"] = "missing required scope"
            }));
        using var viewModel = new ViewVideoDetailViewModel(
            new DesktopInteractionContextStub(dialogService),
            new ClipboardServiceStub(),
            settings,
            workflow,
            new VideoDetailDownloadCoordinatorStub(),
            NullLogger<ViewVideoDetailViewModel>.Instance);
        var completion = WaitUntilExecutableAfterDisabled(viewModel.ParseAllVideoCommand);

        viewModel.ParseAllVideoCommand.Execute(null);
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(workflow.LastParseScope);
        Assert.False(workflow.PageStreamsStarted.Task.IsCompleted);
    }

    [AvaloniaFact]
    public async Task AutoDownloadPreparationRejectsCompetingOperationsAndRecovers()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-auto-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        settings.Update(current => current with
        {
            Basic = current.Basic with
            {
                IsAutoParseVideo = AllowStatus.Yes,
                IsAutoDownloadAll = AllowStatus.Yes,
                ParseScope = ParseScope.All
            }
        });
        var page = new DownKyi.Presentation.VideoPage
        {
            Bvid = "BV1G1421D7mL",
            IsSelected = true
        };
        var section = new DownKyi.Presentation.VideoSection
        {
            IsSelected = true,
            VideoPages = new List<DownKyi.Presentation.VideoPage> { page }
        };
        using var workflow = new VideoDetailWorkflowCoordinatorStub
        {
            DetailResult = new VideoDetailParseResult(
                new DownKyi.Presentation.VideoInfoView(),
                new[] { section })
        };
        var downloadCoordinator = new VideoDetailDownloadCoordinatorStub
        {
            WaitForRelease = true
        };
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
            "https://www.bilibili.com/video/BV1G1421D7mL",
            new AppNavigationParameters()));
        await workflow.PageStreamsStarted.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(viewModel.UiState.IsBusy);
        Assert.False(viewModel.AddToDownloadCommand.CanExecute(null));

        workflow.ReleasePageStreams();
        await downloadCoordinator.AddRequested.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(VideoDetailDisplayState.Content, viewModel.UiState.DisplayState);
        Assert.False(viewModel.UiState.IsBusy);
        Assert.False(viewModel.InputCommand.CanExecute(null));
        Assert.False(viewModel.ParseCommand.CanExecute(null));
        Assert.False(viewModel.ParseAllVideoCommand.CanExecute(null));
        Assert.False(viewModel.AddToDownloadCommand.CanExecute(null));
        Assert.Equal(3, workflow.OperationStartCount);

        viewModel.InputCommand.Execute(null);
        viewModel.ParseCommand.Execute(null);
        viewModel.ParseAllVideoCommand.Execute(null);
        viewModel.AddToDownloadCommand.Execute(null);

        Assert.Equal(3, workflow.OperationStartCount);
        Assert.Equal(1, downloadCoordinator.AddRequestCount);

        downloadCoordinator.ReleaseAdd();
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(viewModel.InputCommand.CanExecute(null));
        Assert.True(viewModel.ParseCommand.CanExecute(null));
        Assert.True(viewModel.ParseAllVideoCommand.CanExecute(null));
        Assert.True(viewModel.AddToDownloadCommand.CanExecute(null));
        Assert.True(downloadCoordinator.LastIsAll);
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
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-detail-recovery-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        using var workflow = new VideoDetailWorkflowCoordinatorStub
        {
            DetailException = operationException
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
        Assert.True(viewModel.ParseCommand.CanExecute(null));
        Assert.True(viewModel.ParseAllVideoCommand.CanExecute(null));
        Assert.True(viewModel.AddToDownloadCommand.CanExecute(null));
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
                    Source = new Uri("avares://DownKyi.Desktop/Languages/Default.axaml")
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

        public IAppNavigationService Navigation { get; } = new NavigationServiceStub();

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
        private readonly TaskCompletionSource _releasePageStreams =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _version;

        public string CurrentInput { get; private set; } = string.Empty;

        public VideoDetailParseResult DetailResult { get; init; } = VideoDetailParseResult.Empty;

        public Exception? DetailException { get; init; }

        public TaskCompletionSource PageStreamsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int OperationStartCount => Volatile.Read(ref _version);

        public ParseScope? LastParseScope { get; private set; }

        public VideoDetailOperation StartOperation()
        {
            return new VideoDetailOperation(Interlocked.Increment(ref _version), CancellationToken.None);
        }

        public bool IsCurrent(VideoDetailOperation operation)
        {
            return operation.Version == Volatile.Read(ref _version);
        }

        public void Cancel()
        {
        }

        public void Reset()
        {
        }

        public string SetInput(string requestedInput)
        {
            CurrentInput = requestedInput;
            return CurrentInput;
        }

        public void ApplySearch(string? searchText)
        {
        }

        public Task<VideoDetailParseResult> LoadDetailAsync(VideoDetailOperation operation)
        {
            if (DetailException != null)
            {
                return Task.FromException<VideoDetailParseResult>(DetailException);
            }

            return Task.FromResult(DetailResult);
        }

        public Task<VideoStreamParseResult?> LoadPageStreamAsync(
            DownKyi.Presentation.VideoPage page,
            VideoDetailOperation operation)
        {
            throw new NotSupportedException();
        }

        public async Task<IReadOnlyList<VideoStreamParseResult>> LoadPageStreamsAsync(
            IEnumerable<DownKyi.Presentation.VideoSection> sections,
            ParseScope parseScope,
            VideoDetailOperation operation)
        {
            var pages = sections.SelectMany(section => section.VideoPages).ToArray();
            LastParseScope = parseScope;
            PageStreamsStarted.TrySetResult();
            await _releasePageStreams.Task.ConfigureAwait(true);
            return pages.Select(page => new VideoStreamParseResult(page, null)).ToArray();
        }

        public void ReleasePageStreams()
        {
            _releasePageStreams.TrySetResult();
        }

        public void Dispose()
        {
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

        public int? Result { get; init; } = 1;

        public Action? BeforeReturn { get; init; }

        public async Task<int?> AddAsync(
            string input,
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
