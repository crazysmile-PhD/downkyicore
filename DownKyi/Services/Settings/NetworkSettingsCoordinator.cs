using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Settings;
using DownKyi.Utils;

namespace DownKyi.Services.Settings;

internal sealed record NetworkSettingsOptions(
    ImmutableArray<int> MaxCurrentDownloads,
    ImmutableArray<int> Splits,
    ImmutableArray<string> AriaLogLevels,
    ImmutableArray<int> AriaMaxConcurrentDownloads,
    ImmutableArray<int> AriaSplits,
    ImmutableArray<int> AriaMaxConnectionsPerServer,
    ImmutableArray<int> AriaMinSplitSizes,
    ImmutableArray<string> AriaFileAllocations)
{
    public static NetworkSettingsOptions Default { get; } = new(
        Enumerable.Range(1, 10).ToImmutableArray(),
        Enumerable.Range(1, 16).ToImmutableArray(),
        ["DEBUG", "INFO", "NOTICE", "WARN", "ERROR"],
        Enumerable.Range(1, 10).ToImmutableArray(),
        Enumerable.Range(1, 16).ToImmutableArray(),
        Enumerable.Range(1, 16).ToImmutableArray(),
        [1, 2, 4, 8, 10, 16, 32, 64],
        ["NONE", "PREALLOC", "FALLOC"]);
}

internal interface INetworkSettingsCoordinator
{
    NetworkSettingsOptions Options { get; }

    NetworkApplicationSettings Current { get; }

    bool Apply(
        Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
        Func<NetworkApplicationSettings, bool> isApplied,
        bool showFeedback);

    Task<bool> ApplyWithRestartPromptAsync(
        Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
        Func<NetworkApplicationSettings, bool> isApplied,
        bool showFeedback,
        CancellationToken cancellationToken = default);
}

internal sealed class NetworkSettingsCoordinator : INetworkSettingsCoordinator
{
    private readonly ISettingsStore _settingsStore;
    private readonly IUserNotificationService _notifications;
    private readonly IAppDialogService _dialogs;
    private readonly IApplicationLifecycle _applicationLifecycle;

    public NetworkSettingsCoordinator(
        ISettingsStore settingsStore,
        IUserNotificationService notifications,
        IAppDialogService dialogs,
        IApplicationLifecycle applicationLifecycle)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _applicationLifecycle = applicationLifecycle
            ?? throw new ArgumentNullException(nameof(applicationLifecycle));
    }

    public NetworkSettingsOptions Options => NetworkSettingsOptions.Default;

    public NetworkApplicationSettings Current => _settingsStore.Current.Network;

    public bool Apply(
        Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
        Func<NetworkApplicationSettings, bool> isApplied,
        bool showFeedback)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(isApplied);

        var network = _settingsStore.Update(settings => settings with
        {
            Network = update(settings.Network)
        }).Network;
        var succeeded = isApplied(network);
        if (showFeedback)
        {
            _notifications.Show(DictionaryResource.GetString(
                succeeded ? "TipSettingUpdated" : "TipSettingFailed"));
        }

        return succeeded;
    }

    public async Task<bool> ApplyWithRestartPromptAsync(
        Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
        Func<NetworkApplicationSettings, bool> isApplied,
        bool showFeedback,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var succeeded = Apply(update, isApplied, showFeedback);
        if (!succeeded || !showFeedback)
        {
            return succeeded;
        }

        var alert = new AlertService(_dialogs);
        var result = await alert
            .ShowInfo(
                DictionaryResource.GetString("ConfirmReboot"),
                cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (result == AppDialogOutcome.Accepted &&
            !await _applicationLifecycle.RestartAsync(cancellationToken).ConfigureAwait(true))
        {
            _notifications.Show("无法重新启动应用，请查看日志");
        }

        return succeeded;
    }
}
