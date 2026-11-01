using DownKyi.Application.Desktop;
using DownKyi.Infrastructure.Settings;
using DownKyi.Services.Download;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Windows.Tests;

public sealed class ViewDownloadSetterWindowsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"downkyi-windows-settings-{Guid.NewGuid():N}");

    [Fact]
    public void UncDownloadDirectoryRemainsSelectableWhenDriveInfoRejectsIt()
    {
        using var settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var viewModel = new ViewDownloadSetterViewModel(
            new StubNotificationService(),
            new StubFilePickerService(),
            settings,
            NullLogger<ViewDownloadSetterViewModel>.Instance);
        viewModel.OnDialogOpened(DownloadSettingsDialog.CreateRequest([]));
        const string directory = @"\\downkyi.invalid\share\downloads";
        AppDialogResult? result = null;
        viewModel.CloseRequested += (_, value) => result = value;

        viewModel.Directory = directory;
        viewModel.DownloadCommand.Execute(null);

        var selection = DownloadSettingsDialog.DecodeResult(Assert.IsType<AppDialogResult>(result));
        Assert.True(selection.HasValue);
        Assert.Equal(directory, selection.Value.Directory);
        Assert.Equal(directory, settings.Current.Video.SaveVideoRootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class StubNotificationService : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

        public void Show(string message)
        {
            NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
        }
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> SelectVideosAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
