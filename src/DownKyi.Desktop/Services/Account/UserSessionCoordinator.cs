using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;

namespace DownKyi.Services.Account;

internal sealed record UserSessionSnapshot(
    UserInfoForNavigation? UserInfo,
    bool HasLoginFile);

internal interface IUserSessionCoordinator
{
    Task<UserSessionSnapshot> RefreshAsync(CancellationToken cancellationToken);
}

internal sealed class UserSessionCoordinator : IUserSessionCoordinator
{
    private readonly ISettingsStore _settingsStore;
    private readonly Func<CancellationToken, Task<UserInfoForNavigation?>> _fetchNavigationAsync;

    public UserSessionCoordinator(
        ISettingsStore settingsStore,
        IBilibiliApiClient client)
        : this(
            settingsStore,
            (client ?? throw new ArgumentNullException(nameof(client)))
            .GetUserInfoForNavigationAsync)
    {
    }

    internal UserSessionCoordinator(
        ISettingsStore settingsStore,
        Func<CancellationToken, Task<UserInfoForNavigation?>> fetchNavigationAsync)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _fetchNavigationAsync = fetchNavigationAsync
                                ?? throw new ArgumentNullException(nameof(fetchNavigationAsync));
    }

    public async Task<UserSessionSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userInfo = await _fetchNavigationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _settingsStore.Update(settings =>
        {
            var mapped = MapSettings(userInfo);
            var keys = new WbiKeys(mapped.ImgKey, mapped.SubKey);
            if (!keys.IsValid)
            {
                mapped = mapped with
                {
                    ImgKey = settings.User.ImgKey,
                    SubKey = settings.User.SubKey
                };
            }

            return settings with { User = mapped };
        });
        cancellationToken.ThrowIfCancellationRequested();
        return new UserSessionSnapshot(userInfo, File.Exists(ApplicationStorage.GetLogin()));
    }

    internal static UserApplicationSettings MapSettings(UserInfoForNavigation? userInfo)
    {
        if (userInfo == null)
        {
            return new UserApplicationSettings(
                Mid: -1,
                Name: string.Empty,
                IsLogin: false,
                IsVip: false,
                ImgKey: string.Empty,
                SubKey: string.Empty);
        }

        var wbi = userInfo.Wbi;
        return new UserApplicationSettings(
            userInfo.Mid,
            userInfo.Name,
            userInfo.IsLogin,
            userInfo.VipStatus == 1,
            WbiKeyProvider.ExtractKey(wbi?.ImageAddress),
            WbiKeyProvider.ExtractKey(wbi?.SubAddress));
    }
}
