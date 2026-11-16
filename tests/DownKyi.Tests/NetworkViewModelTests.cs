using DownKyi.Application.Settings;
using DownKyi.Services.Settings;
using DownKyi.ViewModels.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class NetworkViewModelTests
{
    [Fact]
    public void DownloadLimitsSaturatePositiveValuesLargerThanInt64()
    {
        var coordinator = new RecordingNetworkSettingsCoordinator();
        using var viewModel = new NetworkViewModel(
            new TestDesktopInteractionContext(),
            coordinator,
            NullLogger<NetworkViewModel>.Instance);
        var oversizedValue = new string('9', 100);

        viewModel.MaxOverallDownloadLimitCommand.Execute(oversizedValue);
        viewModel.MaxDownloadLimitCommand.Execute(oversizedValue);

        Assert.Equal(int.MaxValue, viewModel.MaxOverallDownloadLimit);
        Assert.Equal(int.MaxValue, viewModel.MaxDownloadLimit);
        Assert.Equal(int.MaxValue, coordinator.Current.MaxOverallDownloadLimit);
        Assert.Equal(int.MaxValue, coordinator.Current.MaxDownloadLimit);
    }

    private sealed class RecordingNetworkSettingsCoordinator : INetworkSettingsCoordinator
    {
        public NetworkSettingsOptions Options => NetworkSettingsOptions.Default;

        public NetworkApplicationSettings Current { get; private set; } = new(
            AllowStatus.No,
            string.Empty,
            AllowStatus.No,
            NetworkProxy.System,
            string.Empty,
            1,
            1,
            0,
            0,
            AllowStatus.No,
            string.Empty,
            0);

        public bool Apply(
            Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
            Func<NetworkApplicationSettings, bool> isApplied,
            bool showFeedback)
        {
            Current = update(Current);
            return isApplied(Current);
        }

        public Task<bool> ApplyWithRestartPromptAsync(
            Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
            Func<NetworkApplicationSettings, bool> isApplied,
            bool showFeedback,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Apply(update, isApplied, showFeedback));
        }
    }
}
