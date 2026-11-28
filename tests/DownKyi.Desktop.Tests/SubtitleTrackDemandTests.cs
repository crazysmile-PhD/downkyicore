using Avalonia.Controls;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.ViewModels.Dialogs;
using DownKyi.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class SubtitleTrackDemandTests
{
    [AvaloniaFact]
    public async Task DialogFactoryLeavesAsyncModelDisposalWithTheWindow()
    {
        var fixture = new Fixture(enabled: false);
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var services = new ServiceCollection()
            .AddSingleton<IUserNotificationService>(new Notifications())
            .AddSingleton<IFilePickerService>(new UnusedPicker())
            .AddSingleton<ISettingsStore>(fixture.Settings)
            .AddSingleton<ISubtitleTrackLoader>(fixture.Loader)
            .AddSingleton<ILogger<DownloadSetterViewModel>>(NullLogger<DownloadSetterViewModel>.Instance)
            .AddTransient<DownloadSetterDialog>()
            .AddTransient<DownloadSetterViewModel>()
            .BuildServiceProvider();
        var (_, model) = new DialogContentFactory(services).Create(AppDialog.DownloadSettings);

        await AvaloniaDialogService.CompleteViewModelLifecycleAsync(model).ConfigureAwait(true);
        // The synchronous using would throw if the root had captured the async-only model, even after window cleanup.
    }

    [AvaloniaFact]
    public async Task DisabledSubtitlesOpenAndCancelWithoutLookupOrPreferenceChanges()
    {
        var fixture = new Fixture(enabled: false);
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        var original = fixture.Settings.Current.Video;
        var page = fixture.Open();
        Assert.Empty(fixture.Loader.Requests);
        Assert.Equal(DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.NotAttempted, page.SubtitleDiscoveryStatus);
        page.CloseCommand.Execute(null);
        await AvaloniaDialogService.CompleteViewModelLifecycleAsync(page).ConfigureAwait(true);
        Assert.Empty(fixture.Loader.Requests);
        Assert.Equal(original, fixture.Settings.Current.Video);
    }

    [AvaloniaFact]
    public async Task SettingsAreVisibleDuringLookupAndNonSubtitleDownloadCanConfirm()
    {
        var fixture = new Fixture(enabled: true);
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        var page = fixture.Open();
        var request = Assert.Single(fixture.Loader.Requests);
        var view = new DownloadSetterDialog { DataContext = page };
        var window = new Window { Content = view, Width = 680, Height = 640 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(window.IsVisible);
            Assert.True(view.FindControl<ProgressBar>("SubtitleTrackLoading")!.IsVisible);
            Assert.False(page.DownloadCommand.CanExecute(null));
            AppDialogResult? result = null;
            page.CloseRequested += (_, value) => result = value;
            page.DownloadCommand.Execute(null);
            Assert.Null(result);

            page.DownloadSubtitle = false;
            Assert.True(page.DownloadCommand.CanExecute(null));
            page.DownloadCommand.Execute(null);
            var selection = DownloadSettingsDialog.DecodeResult(Assert.IsType<AppDialogResult>(result));
            Assert.True(selection.HasValue);
            Assert.False(selection.Value.RequestedContent.Subtitle);
            Assert.Null(selection.Value.RequestedContent.SelectedSubtitleTrackIds);
            var closing = AvaloniaDialogService.CompleteViewModelLifecycleAsync(page);
            Assert.True(request.Token.IsCancellationRequested);
            request.Response.SetResult(Available());
            await closing.ConfigureAwait(true);
            Assert.Empty(page.SubtitleTracks);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LoadedTracksUsePreferencesAndPreserveManualIdsAndDefaultAcrossOtherOptions()
    {
        var fixture = new Fixture(enabled: false);
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        var page = fixture.Open();
        page.DownloadSubtitle = true;
        page.ContentSelectionChangedCommand.Execute(null);
        page.DownloadSubtitle = false;
        page.DownloadSubtitle = true;
        Assert.Single(fixture.Loader.Requests);
        fixture.Loader.Requests[0].Response.SetResult(Available());
        await DrainAsync().ConfigureAwait(true);
        var preferred = Assert.Single(page.SubtitleTracks, track => track.IsSelected);
        Assert.Equal(44, preferred.TrackId);
        Assert.True(preferred.IsDefault);
        var manual = page.SubtitleTracks.Single(track => track.TrackId == 33);
        manual.IsSelected = true;
        preferred.IsSelected = false;
        manual.IsDefault = true;
        page.DownloadCover = !page.DownloadCover;
        page.ContentSelectionChangedCommand.Execute(null);
        Assert.True(manual.IsSelected);
        Assert.True(manual.IsDefault);
        Assert.Single(fixture.Loader.Requests);
        AppDialogResult? result = null;
        page.CloseRequested += (_, value) => result = value;
        page.DownloadCommand.Execute(null);
        var selection = DownloadSettingsDialog.DecodeResult(Assert.IsType<AppDialogResult>(result));
        Assert.True(selection.HasValue);
        Assert.True(selection.Value.RequestedContent.Subtitle);
        Assert.Equal(33, Assert.Single(selection.Value.RequestedContent.SelectedSubtitleTrackIds!.Value));
        Assert.Equal(33, selection.Value.RequestedContent.DefaultSubtitleTrackId);
        Assert.Equal(SubtitleLanguagePreference.English, fixture.Settings.Current.Video.Content.PreferredSubtitleLanguage);
        Assert.Equal(SubtitleTypePreference.Ai, fixture.Settings.Current.Video.Content.PreferredSubtitleType);
    }

    [AvaloniaFact]
    public async Task FailedLookupCanRetryAndNoResourceKeepsFallbackPreferenceSemantics()
    {
        var fixture = new Fixture(enabled: true);
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        var page = fixture.Open();
        fixture.Loader.Requests[0].Response.SetResult(new(DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.Failed, []));
        await DrainAsync().ConfigureAwait(true);
        Assert.True(page.HasSubtitleLoadFailed);
        Assert.True(page.RetrySubtitleTracksCommand.CanExecute(null));
        page.RetrySubtitleTracksCommand.Execute(null);
        page.RetrySubtitleTracksCommand.Execute(null);
        Assert.Equal(2, fixture.Loader.Requests.Count);
        fixture.Loader.Requests[1].Response.SetResult(new(DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.NoResource, []));
        await DrainAsync().ConfigureAwait(true);
        Assert.False(page.HasSubtitleLoadFailed);
        Assert.Equal(DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.NoResource, page.SubtitleDiscoveryStatus);
        AppDialogResult? result = null;
        page.CloseRequested += (_, value) => result = value;
        page.DownloadCommand.Execute(null);
        var selection = DownloadSettingsDialog.DecodeResult(Assert.IsType<AppDialogResult>(result));
        Assert.True(selection.HasValue);
        Assert.Null(selection.Value.RequestedContent.SelectedSubtitleTrackIds);
        Assert.Equal(SubtitleLanguagePreference.English, selection.Value.RequestedContent.PreferredSubtitleLanguage);
        Assert.Equal(SubtitleTypePreference.Ai, selection.Value.RequestedContent.PreferredSubtitleType);
    }

    [AvaloniaFact]
    public async Task ClosingCancelsAndObservesQueryAndLateReplyCannotUpdateAnotherDialog()
    {
        var fixture = new Fixture(enabled: true);
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        var previous = fixture.Open();
        var oldRequest = fixture.Loader.Requests[0];
        var closing = AvaloniaDialogService.CompleteViewModelLifecycleAsync(previous);
        Assert.True(oldRequest.Token.IsCancellationRequested);
        Assert.False(closing.IsCompleted);
        var current = fixture.Open();
        oldRequest.Response.SetResult(Available());
        await closing.ConfigureAwait(true);
        Assert.Empty(previous.SubtitleTracks);
        Assert.Empty(current.SubtitleTracks);
        Assert.True(current.IsSubtitleLoading);
        fixture.Loader.Requests[1].Response.SetResult(Available());
        await DrainAsync().ConfigureAwait(true);
        Assert.Equal(3, current.SubtitleTracks.Count);
    }

    [AvaloniaFact]
    public async Task PreferenceEditsDuringLookupDriveSelectionAndPersistOnlyOnConfirmation()
    {
        var fixture = new Fixture(enabled: true);
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        var page = fixture.Open();
        page.SelectedSubtitleLanguagePreference = page.SubtitleLanguagePreferences.Single(item =>
            item.Value == SubtitleLanguagePreference.ChineseSimplified);
        page.SelectedSubtitleTypePreference = page.SubtitleTypePreferences.Single(item =>
            item.Value == SubtitleTypePreference.Standard);
        fixture.Loader.Requests[0].Response.SetResult(Available());
        await DrainAsync().ConfigureAwait(true);
        Assert.Equal(11, Assert.Single(page.SubtitleTracks, track => track.IsSelected).TrackId);
        Assert.Equal(SubtitleLanguagePreference.English, fixture.Settings.Current.Video.Content.PreferredSubtitleLanguage);
        Assert.Equal(SubtitleTypePreference.Ai, fixture.Settings.Current.Video.Content.PreferredSubtitleType);
        page.DownloadCommand.Execute(null);
        Assert.Equal(SubtitleLanguagePreference.ChineseSimplified, fixture.Settings.Current.Video.Content.PreferredSubtitleLanguage);
        Assert.Equal(SubtitleTypePreference.Standard, fixture.Settings.Current.Video.Content.PreferredSubtitleType);
    }

    private static DownloadSettingsDialog.SubtitleTrackDiscovery Available() => new(
        DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.Available,
        [new(11, "zh", "中文", 0, "//example.test/zh"), new(33, "en", "English", 0, "//example.test/en"),
            new(44, "ai-en", "English AI", 1, "//example.test/en-ai")]);

    private static async Task DrainAsync() =>
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"downkyi-subtitle-demand-{Guid.NewGuid():N}");
        private readonly List<DownloadSetterViewModel> _pages = [];
        public SettingsStore Settings { get; }
        public ControlledLoader Loader { get; } = new();
        public Fixture(bool enabled)
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            DesktopTestResources.EnsureProductThemeResources();
            Directory.CreateDirectory(_directory);
            Settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
            Settings.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    SaveVideoRootPath = _directory,
                    Content = settings.Video.Content with
                    {
                        DownloadSubtitle = enabled,
                        PreferredSubtitleLanguage = SubtitleLanguagePreference.English,
                        PreferredSubtitleType = SubtitleTypePreference.Ai
                    }
                }
            });
        }
        public DownloadSetterViewModel Open()
        {
            var page = new DownloadSetterViewModel(new Notifications(), new UnusedPicker(), Settings, Loader,
                NullLogger<DownloadSetterViewModel>.Instance);
            _pages.Add(page);
            page.OnDialogOpened(DownloadSettingsDialog.CreateRequestForPage(new(1, "BV1test", 2)));
            return page;
        }
        public async ValueTask DisposeAsync()
        {
            var closing = _pages.Select(AvaloniaDialogService.CompleteViewModelLifecycleAsync).ToArray();
            foreach (var request in Loader.Requests) request.Response.TrySetCanceled(request.Token);
            await Task.WhenAll(closing).ConfigureAwait(true);
            await Settings.DisposeAsync().ConfigureAwait(true);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
    private sealed class ControlledLoader : ISubtitleTrackLoader
    {
        public List<Request> Requests { get; } = [];
        public Task<DownloadSettingsDialog.SubtitleTrackDiscovery> LoadAsync(DownloadSettingsDialog.SubtitlePage page,
            CancellationToken cancellationToken)
        {
            var request = new Request(page, new(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            Requests.Add(request);
            return request.Response.Task;
        }
    }
    private sealed record Request(DownloadSettingsDialog.SubtitlePage Page,
        TaskCompletionSource<DownloadSettingsDialog.SubtitleTrackDiscovery> Response, CancellationToken Token);
    private sealed class Notifications : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised { add { } remove { } }
        public void Show(string message) { }
    }
    private sealed class UnusedPicker : IFilePickerService
    {
        public Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SelectVideosAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
