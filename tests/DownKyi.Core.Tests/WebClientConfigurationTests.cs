using System.Net;
using DownKyi.Core.BiliApi;
using DownKyi.Core.Settings;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Core.Tests;

public sealed class WebClientConfigurationTests
{
    [Fact]
    public void RequestRequiresTheApplicationHostToConfigureTheClient()
    {
        BiliWebClient.ResetConfigurationForTests();
        BiliWebClient.SetBuvidForTests();

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                BiliWebClient.RequestWeb(
                    "https://example.com/getLogin",
                    retry: 1,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("application host", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            BiliWebClient.ClearTestOverrides();
        }
    }

    [Fact]
    public async Task HttpConfigurationUsesOnlyTheInjectedSettingsOwner()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-http-settings-{Guid.NewGuid():N}");

        try
        {
            using var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Update(settings => settings with
            {
                Network = settings.Network with
                {
                    UserAgent = "DownKyi-Test-Agent",
                    NetworkProxy = NetworkProxy.Custom,
                    CustomNetworkProxy = "http://127.0.0.1:18080"
                }
            });

            using var httpClient = new HttpClient();
            BiliWebClient.ConfigureDefaults(httpClient, store);
            using var handler = BiliWebClient.CreateSocketsHandler(store);

            Assert.Equal("DownKyi-Test-Agent", httpClient.DefaultRequestHeaders.UserAgent.ToString());
            Assert.True(handler.UseProxy);
            var proxy = Assert.IsType<WebProxy>(handler.Proxy);
            Assert.Equal(new Uri("http://127.0.0.1:18080"), proxy.Address);

            await store.FlushAsync(TestContext.Current.CancellationToken);
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
