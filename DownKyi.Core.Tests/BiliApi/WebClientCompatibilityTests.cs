using DownKyi.Core.BiliApi.Http;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;
using Xunit;

namespace DownKyi.Core.Tests.BiliApi;

[CollectionDefinition("WebClient static facade", DisableParallelization = true)]
public class WebClientStaticFacadeCollection
{
}

[Collection("WebClient static facade")]
public class WebClientCompatibilityTests
{
    [Fact]
    public void RequestWeb_UsesLegacyStaticApiAndReturnsStringValue()
    {
        BiliWebClient.SetHttpClientForTesting(new FakeBiliHttpClient(ApiResult<string>.Success("legacy-ok")));

        try
        {
            var result = BiliWebClient.RequestWeb("https://example.test/api");

            Assert.Equal("legacy-ok", result);
        }
        finally
        {
            BiliWebClient.ResetHttpClientForTesting();
        }
    }

    [Fact]
    public void RequestWeb_WhenNewClientFails_PreservesLegacyEmptyStringFailureBehavior()
    {
        BiliWebClient.SetHttpClientForTesting(new FakeBiliHttpClient(ApiResult<string>.Failure("failure")));

        try
        {
            var result = BiliWebClient.RequestWeb("https://example.test/api");

            Assert.Equal(string.Empty, result);
        }
        finally
        {
            BiliWebClient.ResetHttpClientForTesting();
        }
    }

    private class FakeBiliHttpClient : IBiliHttpClient
    {
        private readonly ApiResult<string> _stringResult;

        public FakeBiliHttpClient(ApiResult<string> stringResult)
        {
            _stringResult = stringResult;
        }

        public Task<ApiResult<string>> GetStringAsync(
            string url,
            string? referer = null,
            Dictionary<string, object?>? parameters = null,
            int retry = 2,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_stringResult);
        }

        public Task<ApiResult<Stream>> GetStreamAsync(
            string url,
            string? referer = null,
            string method = "GET",
            int retry = 2,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ApiResult<Stream>.Success(new MemoryStream()));
        }


        public Task<ApiResult<bool>> DownloadFileAsync(
            string url,
            string destFile,
            string? referer = null,
            int retry = 2,
            CancellationToken cancellationToken = default)
        {
            File.WriteAllText(destFile, _stringResult.Value ?? string.Empty);
            return Task.FromResult(ApiResult<bool>.Success(true));
        }

        public Task<ApiResult<string>> SendAsync(
            string url,
            string? referer = null,
            string method = "GET",
            Dictionary<string, object?>? parameters = null,
            int retry = 2,
            bool json = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_stringResult);
        }
    }
}
