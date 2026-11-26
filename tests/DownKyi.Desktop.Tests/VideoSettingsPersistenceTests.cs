using Avalonia.Controls;
using DownKyi.Application.Desktop;
using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;
using DownKyi.Utils;
using DownKyi.ViewModels.Settings;
using DownKyi.Views.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class VideoSettingsPersistenceTests
{
    [AvaloniaFact]
    public async Task EmbeddedCoverSwitchPersistsWithoutChangingCoverDownloadSelection()
    {
        DesktopTestResources.EnsureDownloadProjectionResources();
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-embedded-cover-settings-{Guid.NewGuid():N}.json");
        try
        {
            using var store = new SettingsStore(settingsPath);
            using var navigation = new AvaloniaNavigationService(
                _ => throw new InvalidOperationException("Navigation is not expected."),
                action => action());
            var interactions = new DesktopInteractionContext(
                new RecordingNotificationService(),
                navigation,
                new DialogStub());
            using var viewModel = new VideoViewModel(
                interactions,
                new SelectedFolderFilePicker("D:/DownKyi"),
                store,
                NullLogger<VideoViewModel>.Instance);
            var navigationContext = new AppNavigationContext(
                AppNavigationRegion.Settings,
                AppRoute.SettingsVideo,
                AppRoute.Settings,
                null,
                new AppNavigationParameters());
            viewModel.OnNavigatedTo(navigationContext);
            var view = new ViewVideo { DataContext = viewModel };
            var embedCover = Assert.IsType<CheckBox>(view.FindControl<CheckBox>("NameEmbedCover"));
            Assert.True(embedCover.IsChecked);
            Assert.Same(viewModel.EmbedCoverCommand, embedCover.Command);

            foreach (var enabled in new[] { false, true, false })
            {
                embedCover.IsChecked = enabled;
                embedCover.Command!.Execute(null);

                Assert.Equal(enabled, viewModel.EmbedCover);
                Assert.Equal(enabled, store.Current.Video.EmbedCover);
                Assert.True(store.Current.Video.Content.DownloadCover);
            }

            await store.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            using var reopened = new SettingsStore(settingsPath);
            using var restoredViewModel = new VideoViewModel(
                interactions,
                new SelectedFolderFilePicker("D:/DownKyi"),
                reopened,
                NullLogger<VideoViewModel>.Instance);
            restoredViewModel.OnNavigatedTo(navigationContext);

            Assert.False(restoredViewModel.EmbedCover);
            Assert.True(restoredViewModel.DownloadCover);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [AvaloniaFact]
    public async Task DownloadDirectoryCommitFailureKeepsTheOldValueAndShowsFailure()
    {
        DesktopTestResources.EnsureDownloadProjectionResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-video-settings-{Guid.NewGuid():N}.json");
        try
        {
            using var persistedStore = new SettingsStore(settingsPath);
            using var store = new RejectingSettingsStore(persistedStore);
            var notifications = new RecordingNotificationService();
            using var navigation = new AvaloniaNavigationService(
                _ => throw new InvalidOperationException("Navigation is not expected."),
                action => action());
            var interactions = new DesktopInteractionContext(
                notifications,
                navigation,
                new DialogStub());
            const string selectedDirectory = "D:/DownKyi/Rejected";
            using var viewModel = new VideoViewModel(
                interactions,
                new SelectedFolderFilePicker(selectedDirectory),
                store,
                NullLogger<VideoViewModel>.Instance);
            var originalDirectory = store.Current.Video.SaveVideoRootPath;
            viewModel.SaveVideoDirectory = originalDirectory;

            viewModel.ChangeSaveVideoDirectoryCommand.Execute(null);
            var message = await notifications.Message.Task.WaitAsync(
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(DictionaryResource.GetString("TipSettingFailed"), message);
            Assert.NotEqual(DictionaryResource.GetString("TipSettingUpdated"), message);
            Assert.Equal(originalDirectory, store.Current.Video.SaveVideoRootPath);
            Assert.Equal(originalDirectory, viewModel.SaveVideoDirectory);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    private sealed class RejectingSettingsStore(ISettingsStore inner) : ISettingsStore
    {
        public ApplicationSettings Current => inner.Current;

        public ApplicationSettings Update(Func<ApplicationSettings, ApplicationSettings> update) =>
            inner.Update(update);

        public Task<SettingsCommitResult> CommitAsync(
            Func<ApplicationSettings, ApplicationSettings> update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SettingsCommitResult(
                Current,
                SettingsCommitFailure.WriteFailed));
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SelectedFolderFilePicker(string selectedDirectory) : IFilePickerService
    {
        public Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(selectedDirectory);
        }

        public Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> SelectVideosAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingNotificationService : IUserNotificationService
    {
        public TaskCompletionSource<string> Message { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

        public void Show(string message)
        {
            NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
            Message.TrySetResult(message);
        }
    }

    private sealed class DialogStub : IAppDialogService
    {
        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppDialogResult(
                AppDialogOutcome.Canceled,
                new Dictionary<string, object?>()));
        }
    }
}
