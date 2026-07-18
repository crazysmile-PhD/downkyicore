using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Settings;

namespace DownKyi.Core.BiliApi.Sign;

public interface IWbiKeyProvider
{
    Task<WbiKeys> GetValidKeysAsync(CancellationToken cancellationToken);

    Task<WbiKeys> RefreshAsync(CancellationToken cancellationToken);
}

public sealed class WbiKeyProvider : IWbiKeyProvider, IDisposable
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(6);
    private readonly ISettingsStore _settingsStore;
    private readonly Func<CancellationToken, Task<UserInfoForNavigation?>> _fetchNavigationAsync;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _keyLifetime;
    private readonly CancellationTokenSource _lifetimeTokenSource = new();
    private readonly object _sync = new();
    private Task<WbiKeys>? _refreshTask;
    private WbiKeys? _currentKeys;
    private DateTimeOffset _expiresAt;
    private bool _persistedKeysExamined;
    private bool _disposed;

    public WbiKeyProvider(ISettingsStore settingsStore)
        : this(settingsStore, FetchNavigationAsync, TimeProvider.System, DefaultLifetime)
    {
    }

    internal WbiKeyProvider(
        ISettingsStore settingsStore,
        Func<CancellationToken, Task<UserInfoForNavigation?>> fetchNavigationAsync,
        TimeProvider timeProvider,
        TimeSpan keyLifetime)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _fetchNavigationAsync = fetchNavigationAsync
                                ?? throw new ArgumentNullException(nameof(fetchNavigationAsync));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keyLifetime, TimeSpan.Zero);
        _keyLifetime = keyLifetime;
    }

    public Task<WbiKeys> GetValidKeysAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_currentKeys is { IsValid: true } current
                && _timeProvider.GetUtcNow() < _expiresAt)
            {
                return Task.FromResult(current);
            }

            if (!_persistedKeysExamined)
            {
                _persistedKeysExamined = true;
                var user = _settingsStore.Current.User;
                var persisted = new WbiKeys(user.ImgKey, user.SubKey);
                if (persisted.IsValid)
                {
                    PublishInMemory(persisted);
                    return Task.FromResult(persisted);
                }
            }

            return WaitForRefreshAsync(GetOrStartRefresh(), cancellationToken);
        }
    }

    public Task<WbiKeys> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            return WaitForRefreshAsync(GetOrStartRefresh(), cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetimeTokenSource.Cancel();
        _lifetimeTokenSource.Dispose();
    }

    public static string ExtractKey(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var fileName = address[(address.LastIndexOf('/') + 1)..];
        var suffixIndex = fileName.IndexOfAny(['?', '#']);
        if (suffixIndex >= 0)
        {
            fileName = fileName[..suffixIndex];
        }

        var extensionIndex = fileName.IndexOf('.', StringComparison.Ordinal);
        return extensionIndex < 0 ? fileName : fileName[..extensionIndex];
    }

    private static Task<UserInfoForNavigation?> FetchNavigationAsync(CancellationToken cancellationToken)
    {
        return Task.Run(
            () => UserInfo.GetUserInfoForNavigation(cancellationToken),
            cancellationToken);
    }

    private Task<WbiKeys> GetOrStartRefresh()
    {
        if (_refreshTask is not { IsCompleted: false })
        {
            _refreshTask = RefreshCoreAsync(_lifetimeTokenSource.Token);
        }

        return _refreshTask;
    }

    private async Task<WbiKeys> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var navigation = await _fetchNavigationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var wbi = navigation?.Wbi ?? throw new BilibiliApiResponseException(
            nameof(RefreshAsync),
            "Bilibili navigation response did not contain WBI key metadata.");
        var keys = new WbiKeys(ExtractKey(wbi.ImageAddress), ExtractKey(wbi.SubAddress));
        if (!keys.IsValid)
        {
            throw new BilibiliApiResponseException(
                nameof(RefreshAsync),
                "Bilibili navigation response contained invalid WBI keys.");
        }

        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            _settingsStore.Update(settings => settings with
            {
                User = settings.User with
                {
                    ImgKey = keys.ImgKey,
                    SubKey = keys.SubKey
                }
            });
            PublishInMemory(keys);
        }

        return keys;
    }

    private static async Task<WbiKeys> WaitForRefreshAsync(
        Task<WbiKeys> refreshTask,
        CancellationToken cancellationToken)
    {
        return await refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PublishInMemory(WbiKeys keys)
    {
        _currentKeys = keys;
        _expiresAt = _timeProvider.GetUtcNow() + _keyLifetime;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
