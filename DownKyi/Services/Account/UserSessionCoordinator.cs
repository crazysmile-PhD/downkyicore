using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly Func<CancellationToken, UserInfoForNavigation?> _fetchNavigation;

    public UserSessionCoordinator(ISettingsStore settingsStore)
        : this(settingsStore, UserInfo.GetUserInfoForNavigation)
    {
    }

    internal UserSessionCoordinator(
        ISettingsStore settingsStore,
        Func<CancellationToken, UserInfoForNavigation?> fetchNavigation)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _fetchNavigation = fetchNavigation ?? throw new ArgumentNullException(nameof(fetchNavigation));
    }

    public Task<UserSessionSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userInfo = _fetchNavigation(cancellationToken);
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
            return new UserSessionSnapshot(userInfo, File.Exists(StorageManager.GetLogin()));
        }, cancellationToken);
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
