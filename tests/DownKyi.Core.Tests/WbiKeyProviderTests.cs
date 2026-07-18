using System.Net;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.BiliApi.Video;
using DownKyi.Core.Settings;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Core.Tests;

public sealed class WbiKeyProviderTests : IDisposable
{
    private const string ImgKey = "7cd084941338484aae1ad9425b84077c";
    private const string SubKey = "4932caff0ff746eab6f01bf08b70ac45";
    private readonly List<string> _directories = [];
    private readonly WebClientTestContext _webClientContext = new();

    [Fact]
    public async Task EmptyInitialKeysTriggerRefreshBeforeFirstUse()
    {
        using var store = CreateStore();
        var refreshCount = 0;
        using var provider = CreateProvider(store, _ =>
        {
            refreshCount++;
            return Task.FromResult<UserInfoForNavigation?>(CreateNavigation());
        });

        var keys = await provider.GetValidKeysAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, refreshCount);
        Assert.Equal(ImgKey, keys.ImgKey);
        Assert.Equal(SubKey, keys.SubKey);
        Assert.Equal(ImgKey, store.Current.User.ImgKey);
        Assert.Equal(SubKey, store.Current.User.SubKey);
    }

    [Fact]
    public async Task VideoRequestCanStartBeforeHomePageUserRefresh()
    {
        using var store = CreateStore();
        var refreshCount = 0;
        using var provider = CreateProvider(store, _ =>
        {
            refreshCount++;
            return Task.FromResult<UserInfoForNavigation?>(CreateNavigation());
        });
        var sample = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "DownKyi.Core.Tests",
            "BiliApi",
            "JsonSamples",
            "video-view-BV1U7V66FEiK.json"), TestContext.Current.CancellationToken);
        BiliWebClient.SendOverrideForTests = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sample)
        };

        var video = await WbiRequestExecutor.ExecuteAsync(
            provider,
            (keys, unixTimeSeconds) => VideoInfo.VideoViewInfo(
                keys,
                unixTimeSeconds,
                "BV1U7V66FEiK",
                cancellationToken: TestContext.Current.CancellationToken),
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.Equal("BV1U7V66FEiK", video?.Bvid);
        Assert.Equal(1, refreshCount);
    }

    [Fact]
    public async Task TenConcurrentCallersShareOneRefresh()
    {
        using var store = CreateStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<UserInfoForNavigation?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCount = 0;
        using var provider = CreateProvider(store, _ =>
        {
            Interlocked.Increment(ref refreshCount);
            started.TrySetResult();
            return release.Task;
        });

        var requests = Enumerable.Range(0, 10)
            .Select(_ => provider.GetValidKeysAsync(TestContext.Current.CancellationToken))
            .ToArray();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref refreshCount));
        release.SetResult(CreateNavigation());
        var results = await Task.WhenAll(requests);

        Assert.All(results, keys => Assert.Equal(ImgKey, keys.ImgKey));
    }

    [Fact]
    public async Task CancelingOneWaiterDoesNotCancelSharedRefresh()
    {
        using var store = CreateStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<UserInfoForNavigation?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = CreateProvider(store, _ =>
        {
            started.TrySetResult();
            return release.Task;
        });
        var survivingWaiter = provider.GetValidKeysAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var canceled = new CancellationTokenSource();
        var canceledWaiter = provider.GetValidKeysAsync(canceled.Token);

        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        release.SetResult(CreateNavigation());

        var keys = await survivingWaiter.ConfigureAwait(true);
        Assert.Equal(ImgKey, keys.ImgKey);
    }

    [Fact]
    public async Task InvalidRefreshDoesNotPublishPartialKeys()
    {
        using var store = CreateStore();
        using var provider = CreateProvider(store, _ => Task.FromResult<UserInfoForNavigation?>(new()
        {
            Wbi = new Wbi
            {
                ImageAddress = $"https://i0.hdslb.com/bfs/wbi/{ImgKey}.png",
                SubAddress = string.Empty
            }
        }));

        await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            provider.GetValidKeysAsync(TestContext.Current.CancellationToken));

        Assert.Empty(store.Current.User.ImgKey);
        Assert.Empty(store.Current.User.SubKey);
    }

    [Fact]
    public async Task ExpiredRuntimeKeysRefreshInsteadOfReusingPersistedSnapshot()
    {
        const string persistedImgKey = "11111111111111111111111111111111";
        const string persistedSubKey = "22222222222222222222222222222222";
        using var store = CreateStore();
        store.Update(settings => settings with
        {
            User = settings.User with
            {
                ImgKey = persistedImgKey,
                SubKey = persistedSubKey
            }
        });
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var refreshCount = 0;
        using var provider = new WbiKeyProvider(
            store,
            _ =>
            {
                refreshCount++;
                return Task.FromResult<UserInfoForNavigation?>(CreateNavigation());
            },
            clock,
            TimeSpan.FromHours(1));

        var persisted = await provider.GetValidKeysAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));
        var refreshed = await provider.GetValidKeysAsync(TestContext.Current.CancellationToken);

        Assert.Equal(persistedImgKey, persisted.ImgKey);
        Assert.Equal(ImgKey, refreshed.ImgKey);
        Assert.Equal(1, refreshCount);
    }

    [Fact]
    public async Task SignatureRejectionRefreshesAndRetriesExactlyOnce()
    {
        var provider = new RecordingKeyProvider();
        var requestCount = 0;

        var result = await WbiRequestExecutor.ExecuteAsync(
            provider,
            (_, _) => ++requestCount == 1
                ? throw new BilibiliApiResponseException("fixture", "signature rejected", code: -403)
                : "ok",
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.Equal("ok", result);
        Assert.Equal(2, requestCount);
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task SecondSignatureRejectionIsNotRetriedAgain()
    {
        var provider = new RecordingKeyProvider();
        var requestCount = 0;

        await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            WbiRequestExecutor.ExecuteAsync<string>(
                provider,
                (_, _) =>
                {
                    requestCount++;
                    throw new BilibiliApiResponseException("fixture", "signature rejected", code: -403);
                },
                TimeProvider.System,
                TestContext.Current.CancellationToken));

        Assert.Equal(2, requestCount);
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task NonSignatureFailureDoesNotRefreshKeys()
    {
        var provider = new RecordingKeyProvider();
        var requestCount = 0;

        await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            WbiRequestExecutor.ExecuteAsync<string>(
                provider,
                (_, _) =>
                {
                    requestCount++;
                    throw new BilibiliApiResponseException("fixture", "not found", code: -404);
                },
                TimeProvider.System,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, requestCount);
        Assert.Equal(0, provider.RefreshCount);
    }

    public void Dispose()
    {
        _webClientContext.Dispose();
        foreach (var directory in _directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        GC.SuppressFinalize(this);
    }

    private SettingsStore CreateStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-wbi-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return new SettingsStore(Path.Combine(directory, "settings.json"));
    }

    private static WbiKeyProvider CreateProvider(
        ISettingsStore store,
        Func<CancellationToken, Task<UserInfoForNavigation?>> refresh)
    {
        return new WbiKeyProvider(
            store,
            refresh,
            TimeProvider.System,
            TimeSpan.FromHours(1));
    }

    private static UserInfoForNavigation CreateNavigation()
    {
        return new UserInfoForNavigation
        {
            Wbi = new Wbi
            {
                ImageAddress = $"https://i0.hdslb.com/bfs/wbi/{ImgKey}.png",
                SubAddress = $"https://i0.hdslb.com/bfs/wbi/{SubKey}.png"
            }
        };
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class RecordingKeyProvider : IWbiKeyProvider
    {
        private static readonly WbiKeys InitialKeys = new(
            "11111111111111111111111111111111",
            "22222222222222222222222222222222");
        private static readonly WbiKeys RefreshedKeys = new(
            "33333333333333333333333333333333",
            "44444444444444444444444444444444");

        public int RefreshCount { get; private set; }

        public Task<WbiKeys> GetValidKeysAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(InitialKeys);
        }

        public Task<WbiKeys> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            return Task.FromResult(RefreshedKeys);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }
}
