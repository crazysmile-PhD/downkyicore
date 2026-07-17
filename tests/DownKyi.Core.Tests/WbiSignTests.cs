using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.Settings;

namespace DownKyi.Core.Tests;

public sealed class WbiSignTests
{
    [Fact]
    public void EncodeWbiMatchesProtocolVector()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["foo"] = 114,
            ["bar"] = 514,
            ["baz"] = 1919810
        };

        var signed = WbiSign.EncodeWbi(
            parameters,
            "7cd084941338484aae1ad9425b84077c",
            "4932caff0ff746eab6f01bf08b70ac45",
            1702204169);

        Assert.Equal("1702204169", signed["wts"]);
        Assert.Equal("6149fdadf571698ca7e6a567265cd0ee", signed["w_rid"]);
    }

    [Fact]
    public async Task EncodeWbiReadsKeysFromTheInjectedSettingsOwner()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-wbi-settings-{Guid.NewGuid():N}");

        try
        {
            using var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Update(settings => settings with
            {
                User = settings.User with
                {
                    ImgKey = "7cd084941338484aae1ad9425b84077c",
                    SubKey = "4932caff0ff746eab6f01bf08b70ac45"
                }
            });
            var parameters = new Dictionary<string, object?>
            {
                ["foo"] = 114,
                ["bar"] = 514,
                ["baz"] = 1919810
            };

            var signed = WbiSign.EncodeWbi(parameters, store, 1702204169);
            await store.FlushAsync(TestContext.Current.CancellationToken);

            Assert.Equal("1702204169", signed["wts"]);
            Assert.Equal("6149fdadf571698ca7e6a567265cd0ee", signed["w_rid"]);
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
