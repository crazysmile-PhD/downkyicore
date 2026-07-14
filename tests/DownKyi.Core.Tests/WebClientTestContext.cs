using DownKyi.Core.BiliApi;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Core.Tests;

internal sealed class WebClientTestContext : IDisposable
{
    private readonly HttpClient _httpClient = new();

    public WebClientTestContext()
    {
        BiliWebClient.Configure(new BilibiliHttpClient(_httpClient));
        BiliWebClient.SetBuvidForTests();
    }

    public void Dispose()
    {
        BiliWebClient.ClearTestOverrides();
        BiliWebClient.ResetConfigurationForTests();
        _httpClient.Dispose();
    }
}
