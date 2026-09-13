using System.Net;
using DownKyi.Application.Bilibili;
using DownKyi.Infrastructure.Bilibili;

namespace DownKyi.Infrastructure.Tests;

public sealed class BilibiliLoginSessionTests
{
    [Fact]
    public async Task PollResponseCapturesSessionCookies()
    {
        using var factory = new TestHttpClientFactory((_, _) =>
            BilibiliTestResponses.CompletedJsonWithCookies(
                """{"code":0,"data":{"code":0}}""",
                "SESSDATA=fixture-session; Domain=.bilibili.com; Path=/; Secure",
                "bili_jct=fixture-csrf; Domain=.bilibili.com; Path=/; Secure"));
        using var session = CreateSession(factory);

        var response = await session.GetAsync(
            new BilibiliHttpRequest(
                "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=fixture",
                includeCredentials: false,
                attempts: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal("""{"code":0,"data":{"code":0}}""", response.Content);
        Assert.Contains(response.Cookies, cookie => cookie.Name == "SESSDATA");
        Assert.Contains(response.Cookies, cookie => cookie.Name == "bili_jct");
    }

    [Fact]
    public async Task HostScopedCookieRemainsInsideTheIsolatedLoginSession()
    {
        using var factory = new TestHttpClientFactory((_, _) =>
            BilibiliTestResponses.CompletedJsonWithCookies(
                """{"code":0,"data":{"code":0}}""",
                "SESSDATA=fixture-session; Domain=.bilibili.com; Path=/; Secure",
                "passport_only=fixture-private; Path=/; Secure"));
        using var session = CreateSession(factory);

        var response = await session.GetAsync(
            new BilibiliHttpRequest(
                "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=fixture",
                includeCredentials: false,
                attempts: 1),
            TestContext.Current.CancellationToken);

        Assert.Contains(response.Cookies, cookie => cookie.Name == "SESSDATA");
        Assert.DoesNotContain(response.Cookies, cookie => cookie.Name == "passport_only");
    }

    [Fact]
    public async Task CallbackCapturesCookiesFromEveryTrustedRedirect()
    {
        var calls = 0;
        using var factory = new TestHttpClientFactory((_, _) => ++calls switch
        {
            1 => CompletedRedirect(
                "https://www.bilibili.com/",
                "DedeUserID=fixture-user; Domain=.bilibili.com; Path=/; Secure"),
            _ => BilibiliTestResponses.CompletedJsonWithCookies(
                string.Empty,
                "sid=fixture-sid; Domain=.bilibili.com; Path=/; Secure")
        });
        using var session = CreateSession(factory);

        var cookies = await session.FollowCallbackAsync(
            new Uri("https://passport.bilibili.com/callback"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.Contains(cookies, cookie => cookie.Name == "DedeUserID");
        Assert.Contains(cookies, cookie => cookie.Name == "sid");
    }

    [Fact]
    public async Task CallbackStopsBeforeExternalHttpsLandingAndReturnsCapturedCookies()
    {
        var calls = 0;
        using var factory = new TestHttpClientFactory((_, _) =>
        {
            calls++;
            return BilibiliTestResponses.CompletedJson();
        });
        using var session = CreateSession(
            factory,
            [new BilibiliLoginCookie("SESSDATA", "fixture-session", ".bilibili.com")]);

        var cookies = await session.FollowCallbackAsync(
            new Uri("https://passport.biligame.com/x/passport-login/web/crossDomain?DedeUserID=fixture-user"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        Assert.Contains(cookies, cookie => cookie.Name == "SESSDATA");
    }

    [Fact]
    public async Task CallbackRedirectStopsBeforeExternalHttpsLanding()
    {
        var calls = 0;
        using var factory = new TestHttpClientFactory((_, _) =>
        {
            calls++;
            return CompletedRedirect(
                "https://passport.biligame.com/x/passport-login/web/crossDomain?DedeUserID=fixture-user",
                "SESSDATA=fixture-session; Domain=.bilibili.com; Path=/; Secure");
        });
        using var session = CreateSession(factory);

        var cookies = await session.FollowCallbackAsync(
            new Uri("https://passport.bilibili.com/callback"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Contains(cookies, cookie => cookie.Name == "SESSDATA");
    }

    [Theory]
    [InlineData("http://passport.bilibili.com/callback")]
    [InlineData("https://bilibili.com.example.invalid/callback")]
    [InlineData("https://user@passport.bilibili.com/callback")]
    [InlineData("https://passport.bilibili.com:8443/callback")]
    [InlineData("https://passport.biligame.com/not-a-login-callback")]
    public async Task CallbackRejectsUntrustedAddressBeforeNetworkWork(string callbackAddress)
    {
        var calls = 0;
        using var factory = new TestHttpClientFactory((_, _) =>
        {
            calls++;
            return BilibiliTestResponses.CompletedJson();
        });
        using var session = CreateSession(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.FollowCallbackAsync(
                new Uri(callbackAddress),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CallbackRejectsUntrustedRedirectBeforeFollowingIt()
    {
        var calls = 0;
        using var factory = new TestHttpClientFactory((_, _) =>
        {
            calls++;
            return CompletedRedirect("https://example.invalid/", "sid=fixture; Path=/; Secure");
        });
        using var session = CreateSession(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.FollowCallbackAsync(
                new Uri("https://passport.bilibili.com/callback"),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    private static BilibiliLoginSession CreateSession(
        TestHttpClientFactory factory,
        IReadOnlyList<BilibiliLoginCookie>? initialCookies = null)
    {
        return new BilibiliLoginSession(
            new BilibiliHttpTransport(
                factory,
                TimeProvider.System,
                static (_, _) => Task.CompletedTask),
            NullDisposable.Instance,
            initialCookies);
    }

    private static Task<HttpResponseMessage> CompletedRedirect(
        string location,
        string setCookieHeader)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        response.Headers.TryAddWithoutValidation("Set-Cookie", setCookieHeader);
        return Task.FromResult(response);
    }

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
