using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Core.Storage;
using DownKyi.Services.Account;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class LoginCoordinatorTests
{
    [Fact]
    public async Task RequestLoginUrlPreservesCancellationBeforeNetworkWork()
    {
        using var session = new StubLoginSession();
        using var coordinator = new LoginCoordinator(
            NullLogger<LoginCoordinator>.Instance,
            new TestBilibiliApiClient(),
            new StubLoginSessionFactory(session));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RequestLoginUrlAsync(cancellation.Token));
    }

    [Fact]
    public async Task SessionCreatedDuringDisposalIsRejectedAndDisposed()
    {
        using var session = new StubLoginSession();
        LoginCoordinator? coordinatorReference = null;
        var factory = new StubLoginSessionFactory(
            session,
            () => coordinatorReference!.Dispose());
        using var coordinator = new LoginCoordinator(
            NullLogger<LoginCoordinator>.Instance,
            new TestBilibiliApiClient(),
            factory);
        coordinatorReference = coordinator;

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.RequestLoginUrlAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, session.DisposeCalls);
    }

    [Fact]
    public async Task SuccessfulLoginPersistsMergedCookiesThatValidateAfterReload()
    {
        LoginHelper.DeleteLoginInfoCookies();
        using var session = new StubLoginSession
        {
            CallbackCookies =
            [
                new BilibiliLoginCookie("bili_jct", "fixture-csrf", ".bilibili.com"),
                new BilibiliLoginCookie("DedeUserID", "fixture-user", ".bilibili.com")
            ]
        };
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                Assert.Contains("/x/web-interface/nav", request.RequestAddress, StringComparison.Ordinal);
                var cookieHeader = LoginHelper.GetLoginInfoCookiesString();
                var isLogin = cookieHeader.Contains("SESSDATA=fixture%2Fsession", StringComparison.Ordinal)
                              && cookieHeader.Contains("bili_jct=fixture-csrf", StringComparison.Ordinal)
                              && cookieHeader.Contains("DedeUserID=fixture-user", StringComparison.Ordinal);
                return Task.FromResult(isLogin
                    ? """{"code":0,"data":{"isLogin":true,"mid":42,"uname":"fixture-user"}}"""
                    : """{"code":-101,"data":{"isLogin":false}}""");
            }
        };
        using var coordinator = new LoginCoordinator(
            NullLogger<LoginCoordinator>.Instance,
            client,
            new StubLoginSessionFactory(session));
        await coordinator.RequestLoginUrlAsync(TestContext.Current.CancellationToken);
        var pollResult = new LoginStatusResult(
            new LoginStatus(),
            [new BilibiliLoginCookie("SESSDATA", "fixture%2Fsession", ".bilibili.com")]);

        var result = await coordinator.SaveLoginCookiesAsync(
            pollResult,
            new Uri(
                "https://passport.bilibili.com/callback?SESSDATA=query-session&ticket=fixture-ticket"),
            TestContext.Current.CancellationToken);

        Assert.True(result);
        var reloaded = LoginHelper.GetLoginInfoCookies();
        Assert.Contains(reloaded, cookie => cookie.Name == "SESSDATA");
        Assert.Contains(reloaded, cookie => cookie.Name == "bili_jct");
        Assert.Contains(reloaded, cookie => cookie.Name == "DedeUserID");
        Assert.Contains(reloaded, cookie => cookie.Name == "ticket");
        Assert.Contains(
            "SESSDATA=fixture%2Fsession",
            LoginHelper.GetLoginInfoCookiesString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserCookiesCommitWithoutQrSessionAndPreserveWireValues()
    {
        LoginHelper.DeleteLoginInfoCookies();
        using var session = new StubLoginSession();
        var sessionFactory = new StubLoginSessionFactory(session);
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) =>
            {
                Assert.Equal(
                    "SESSDATA=fixture%2Fsession",
                    LoginHelper.GetLoginInfoCookiesString());
                return Task.FromResult(
                    """{"code":0,"data":{"isLogin":true,"mid":42,"uname":"fixture-user"}}""");
            }
        };
        using var coordinator = new LoginCoordinator(
            NullLogger<LoginCoordinator>.Instance,
            client,
            sessionFactory);
        var cookies = BrowserCookieParser.ParseHeader(
            "Cookie: SESSDATA=fixture%2Fsession");

        var candidate = Assert.Single(cookies);
        Assert.Equal(".bilibili.com", candidate.Domain);
        Assert.True(candidate.IsWireValue);

        var result = await coordinator.CommitLoginCookiesAsync(
            cookies,
            TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(0, sessionFactory.CreateCalls);
        var reloaded = Assert.Single(LoginHelper.GetLoginInfoCookies());
        Assert.Equal("fixture%2Fsession", reloaded.Value);
        Assert.True(reloaded.IsWireValue);
        Assert.Equal("SESSDATA=fixture%2Fsession", LoginHelper.GetLoginInfoCookiesString());
    }

    [Fact]
    public async Task BrowserInputWithoutValidCookiesDoesNotReplacePreviousLogin()
    {
        var previousCookies = new[]
        {
            new DownKyiCookie("previous", "fixture-previous", ".bilibili.com")
        };
        Assert.True(LoginHelper.SaveLoginInfoCookies(previousCookies));
        using var session = new StubLoginSession();
        var sessionFactory = new StubLoginSessionFactory(session);
        using var coordinator = new LoginCoordinator(
            NullLogger<LoginCoordinator>.Instance,
            new TestBilibiliApiClient(),
            sessionFactory);
        var cookies = BrowserCookieParser.ParseHeader("not-a-cookie; =missing-name; empty=");

        Assert.Empty(cookies);
        var result = await coordinator.CommitLoginCookiesAsync(
            cookies,
            TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(0, sessionFactory.CreateCalls);
        var restored = Assert.Single(LoginHelper.GetLoginInfoCookies());
        Assert.Equal("previous", restored.Name);
        Assert.Equal("fixture-previous", restored.Value);
    }

    [Fact]
    public async Task FailedCandidateValidationRestoresPreviousLoginCookies()
    {
        var previousCookies = new[]
        {
            new DownKyiCookie("previous", "fixture-previous", ".bilibili.com")
        };
        Assert.True(LoginHelper.SaveLoginInfoCookies(previousCookies));
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(
                """{"code":-101,"data":{"isLogin":false}}""")
        };
        using var session = new StubLoginSession();
        using var coordinator = new LoginCoordinator(
            NullLogger<LoginCoordinator>.Instance,
            client,
            new StubLoginSessionFactory(session));
        var result = await coordinator.CommitLoginCookiesAsync(
            [new DownKyiCookie(
                "SESSDATA",
                "invalid-fixture",
                ".bilibili.com",
                isWireValue: true)],
            TestContext.Current.CancellationToken);

        Assert.False(result);
        var restored = Assert.Single(LoginHelper.GetLoginInfoCookies());
        Assert.Equal("previous", restored.Name);
        Assert.Equal("fixture-previous", restored.Value);
    }

    [Fact]
    public async Task ValidationFailureRestoresPreviousLoginCookiesBeforeRethrowingOriginalException()
    {
        var previousCookies = new[]
        {
            new DownKyiCookie("previous", "fixture-previous", ".bilibili.com")
        };
        Assert.True(LoginHelper.SaveLoginInfoCookies(previousCookies));
        var expected = new HttpRequestException("Fixture validation failure.");
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromException<string>(
                expected)
        };
        using var session = new StubLoginSession();
        using var coordinator = new LoginCoordinator(
            NullLogger<LoginCoordinator>.Instance,
            client,
            new StubLoginSessionFactory(session));
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() =>
            coordinator.CommitLoginCookiesAsync(
                [new DownKyiCookie(
                    "SESSDATA",
                    "invalid-fixture",
                    ".bilibili.com",
                    isWireValue: true)],
                TestContext.Current.CancellationToken));

        Assert.Same(expected, thrown);
        var restored = Assert.Single(LoginHelper.GetLoginInfoCookies());
        Assert.Equal("previous", restored.Name);
        Assert.Equal("fixture-previous", restored.Value);
    }

    [Fact]
    public async Task CancellationAfterPersistenceRestoresPreviousLoginCookies()
    {
        var previousCookies = new[]
        {
            new DownKyiCookie("previous", "fixture-previous", ".bilibili.com")
        };
        Assert.True(LoginHelper.SaveLoginInfoCookies(previousCookies));
        using var cancellation = new CancellationTokenSource();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = async (_, _) =>
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                throw new OperationCanceledException(cancellation.Token);
            }
        };
        using var session = new StubLoginSession();
        using var coordinator = new LoginCoordinator(
            NullLogger<LoginCoordinator>.Instance,
            client,
            new StubLoginSessionFactory(session));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.CommitLoginCookiesAsync(
                [new DownKyiCookie(
                    "SESSDATA",
                    "candidate-session",
                    ".bilibili.com",
                    isWireValue: true)],
                cancellation.Token));

        var restored = Assert.Single(LoginHelper.GetLoginInfoCookies());
        Assert.Equal("previous", restored.Name);
        Assert.Equal("fixture-previous", restored.Value);
    }

    private sealed class StubLoginSessionFactory : IBilibiliLoginSessionFactory
    {
        private readonly StubLoginSession _session;
        private readonly Action? _onCreate;

        public int CreateCalls { get; private set; }

        public StubLoginSessionFactory(StubLoginSession session, Action? onCreate = null)
        {
            _session = session;
            _onCreate = onCreate;
        }

        public IBilibiliLoginSession Create(
            IReadOnlyList<BilibiliLoginCookie>? initialCookies = null)
        {
            CreateCalls++;
            _onCreate?.Invoke();
            return _session;
        }
    }

    private sealed class StubLoginSession : IBilibiliLoginSession
    {
        public IReadOnlyList<BilibiliLoginCookie> CallbackCookies { get; init; } = [];

        public int DisposeCalls { get; private set; }

        public Task<BilibiliLoginResponse> GetAsync(
            BilibiliHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new BilibiliLoginResponse(
                """{"code":0,"data":{"url":"https://example.invalid/qr","qrcode_key":"fixture-key"}}""",
                []));
        }

        public Task<IReadOnlyList<BilibiliLoginCookie>> FollowCallbackAsync(
            Uri callbackUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CallbackCookies);
        }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }
}
