using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Settings;
using DownKyi.Core.Settings.Models;
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

    public UserSessionCoordinator(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public Task<UserSessionSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userInfo = UserInfo.GetUserInfoForNavigation();
            cancellationToken.ThrowIfCancellationRequested();
            _settingsStore.Settings.SetUserInfo(MapSettings(userInfo));
            cancellationToken.ThrowIfCancellationRequested();
            return new UserSessionSnapshot(userInfo, File.Exists(StorageManager.GetLogin()));
        }, cancellationToken);
    }

    internal static UserInfoSettings MapSettings(UserInfoForNavigation? userInfo)
    {
        if (userInfo == null)
        {
            return new UserInfoSettings
            {
                Mid = -1,
                Name = string.Empty,
                IsLogin = false,
                IsVip = false
            };
        }

        return new UserInfoSettings
        {
            Mid = userInfo.Mid,
            Name = userInfo.Name,
            IsLogin = userInfo.IsLogin,
            IsVip = userInfo.VipStatus == 1,
            ImgKey = ExtractWbiKey(userInfo.Wbi.ImageAddress),
            SubKey = ExtractWbiKey(userInfo.Wbi.SubAddress)
        };
    }

    private static string ExtractWbiKey(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return string.Empty;
        }

        var fileName = address[(address.LastIndexOf('/') + 1)..];
        var queryIndex = fileName.IndexOf('?', StringComparison.Ordinal);
        var fragmentIndex = fileName.IndexOf('#', StringComparison.Ordinal);
        var suffixIndex = queryIndex < 0
            ? fragmentIndex
            : fragmentIndex < 0 ? queryIndex : Math.Min(queryIndex, fragmentIndex);
        if (suffixIndex >= 0)
        {
            fileName = fileName[..suffixIndex];
        }

        var extensionIndex = fileName.IndexOf('.', StringComparison.Ordinal);
        return extensionIndex < 0 ? fileName : fileName[..extensionIndex];
    }
}
