using System.Net;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Core.Tests;

public sealed class UserNavigationContractTests : IDisposable
{
    private readonly WebClientTestContext _context = new();

    [Fact]
    public void AnonymousNavigationResponsePreservesPublicWbiMetadata()
    {
        ConfigureAnonymousResponse();

        var navigation = UserInfo.GetUserInfoForNavigation(TestContext.Current.CancellationToken);

        Assert.NotNull(navigation);
        Assert.False(navigation.IsLogin);
        Assert.Equal(0, navigation.Mid);
        Assert.Equal("11111111111111111111111111111111", WbiKeyProvider.ExtractKey(navigation.Wbi?.ImageAddress));
        Assert.Equal("22222222222222222222222222222222", WbiKeyProvider.ExtractKey(navigation.Wbi?.SubAddress));
    }

    [Fact]
    public void AnonymousCodeRemainsRejectedOutsideTheNavigationContract()
    {
        ConfigureAnonymousResponse();

        var exception = Assert.Throws<BilibiliApiResponseException>(() =>
            BiliApiRequest.RequestJson<UserInfoForNavigationOrigin>(
                "https://example.test/not-nav",
                referer: null,
                operationName: "ordinary-contract",
                logTag: nameof(UserNavigationContractTests),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(-101, exception.Code);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ConfigureAnonymousResponse()
    {
        var body = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "DownKyi.Core.Tests",
            "BiliApi",
            "JsonSamples",
            "user-navigation-anonymous.json"));
        BiliWebClient.SendOverrideForTests = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
