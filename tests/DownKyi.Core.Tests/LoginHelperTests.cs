using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Settings;
using DownKyi.Core.Settings.Models;

namespace DownKyi.Core.Tests;

public sealed class LoginHelperTests
{
    [Fact]
    public async Task LogoutDeletesTheOwnedLoginFileAndClearsTheInjectedUser()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-logout-{Guid.NewGuid():N}");
        var loginPath = Path.Combine(directory, "login.json");
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(loginPath, "test-login", TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);
            store.Settings.SetUserInfo(new UserInfoSettings
            {
                Mid = 42,
                Name = "test-user",
                IsLogin = true,
                IsVip = true
            });

            var result = LoginHelper.Logout(store, loginPath);
            await store.FlushAsync(TestContext.Current.CancellationToken);

            Assert.True(result);
            Assert.False(File.Exists(loginPath));
            var user = store.Settings.GetUserInfo();
            Assert.Equal(-1, user.Mid);
            Assert.False(user.IsLogin);
            Assert.False(user.IsVip);
            Assert.Empty(user.Name);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
