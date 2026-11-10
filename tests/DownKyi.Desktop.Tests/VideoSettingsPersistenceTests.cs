using DownKyi.Application.Desktop;
using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;
using DownKyi.Utils;
using DownKyi.ViewModels.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class VideoSettingsPersistenceTests
{
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
            using var viewModel = new ViewVideoViewModel(
                interactions,
                new SelectedFolderFilePicker(selectedDirectory),
                store,
                NullLogger<ViewVideoViewModel>.Instance);
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
