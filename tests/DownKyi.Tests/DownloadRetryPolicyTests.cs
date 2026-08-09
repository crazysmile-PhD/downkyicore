using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using DownKyi.Services.Download;
using Downloader.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadRetryPolicyTests
{
    [Fact]
    public async Task CoordinatorUsesOneGlobalBudgetAcrossPrimaryAndBackupAddresses()
    {
        using var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.InvalidMedia,
                "invalid-media"));
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);

        var result = await coordinator.TransferAsync(
            CreateRequest(
                "https://primary.invalid/media",
                "https://backup-1.invalid/media",
                "https://backup-2.invalid/media",
                "https://backup-3.invalid/media"),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Equal(4, backend.Requests.Count);
        Assert.Equal(
            4,
            backend.Requests.SelectMany(request => request.Urls).Distinct().Count());
    }

    [Fact]
    public async Task CoordinatorRetriesTransientFailureOnSameAddressBeforeMoving()
    {
        using var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "network-timeout"),
            DownloadTransferResult.Succeeded());
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);

        var result = await coordinator.TransferAsync(
            CreateRequest("https://primary.invalid/media", "https://backup.invalid/media"),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, backend.Requests.Count);
        Assert.Equal(backend.Requests[0].Urls, backend.Requests[1].Urls);
    }

    [Fact]
    public async Task CoordinatorCarriesLatestBackendIdentityAcrossRetry()
    {
        using var backend = new IdentityPublishingBackend();
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);

        var result = await coordinator.TransferAsync(
            CreateRequest("https://primary.invalid/media"),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
        Assert.Equal([null, "test-backend-id"], backend.ObservedIdentities);
    }

    [Fact]
    public async Task CoordinatorRefreshesExpiredAddressesOnlyOnce()
    {
        using var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ExpiredAddress,
                "http-403"),
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ExpiredAddress,
                "http-403"),
            DownloadTransferResult.Succeeded());
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);
        var refreshCount = 0;

        var result = await coordinator.TransferAsync(
            CreateRequest("https://primary.invalid/media", "https://backup.invalid/media"),
            _ =>
            {
                refreshCount++;
                return Task.FromResult<IReadOnlyList<string>>(
                    ["https://refreshed.invalid/media"]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, refreshCount);
        Assert.Equal(
            "https://refreshed.invalid/media",
            Assert.Single(backend.Requests[2].Urls));
    }

    [Fact]
    public async Task CoordinatorDoesNotRefreshExpiredAddressesTwice()
    {
        using var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ExpiredAddress,
                "http-403"));
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);
        var refreshCount = 0;

        var result = await coordinator.TransferAsync(
            CreateRequest("https://primary.invalid/media"),
            _ =>
            {
                refreshCount++;
                return Task.FromResult<IReadOnlyList<string>>(
                    ["https://refreshed.invalid/media"]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Equal(1, refreshCount);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task CoordinatorDoesNotRetryDiskFailure()
    {
        using var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Disk,
                "disk-write"));
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);

        var result = await coordinator.TransferAsync(
            CreateRequest("https://primary.invalid/media", "https://backup.invalid/media"),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task CoordinatorDoesNotRetryTlsFailureOrTryBackupAddresses()
    {
        using var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Tls,
                "download.transfer.tls.untrusted"));
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);

        var result = await coordinator.TransferAsync(
            CreateRequest("https://primary.invalid/media", "https://backup.invalid/media"),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Equal(DownloadTransferFailureKind.Tls, result.FailureKind);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task CoordinatorRetriesCleanedResumeRejectionOnceBeforeMoving()
    {
        using var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ResumeRejected,
                "download.transfer.aria2-8"),
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ResumeRejected,
                "download.transfer.aria2-8"),
            DownloadTransferResult.Succeeded());
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);

        var result = await coordinator.TransferAsync(
            CreateRequest(
                "https://primary.invalid/media",
                "https://backup.invalid/media"),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, backend.Requests.Count);
        Assert.Equal(backend.Requests[0].Urls, backend.Requests[1].Urls);
        Assert.NotEqual(backend.Requests[1].Urls, backend.Requests[2].Urls);
    }

    [Fact]
    public void PolicyHonorsAndBoundsRetryAfter()
    {
        var policy = new DownloadRetryPolicy();
        var decision = policy.Decide(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.RateLimited,
                "http-429",
                TimeSpan.FromMinutes(5)),
            attempt: 1,
            attemptsForAddress: 1,
            hasNextAddress: true,
            canRefreshAddresses: true);

        Assert.Equal(DownloadRetryAction.RetrySameAddress, decision.Action);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.Delay);
    }

    [Fact]
    public async Task CoordinatorDoesNotSwallowCancellation()
    {
        using var backend = new CancelingBackend();
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.TransferAsync(
                CreateRequest("https://primary.invalid/media"),
                static _ => Task.FromResult<IReadOnlyList<string>>([]),
                cancellation.Token));
        Assert.Equal(0, backend.Attempts);
    }

    [Fact]
    public async Task CoordinatorCancellationInterruptsBackoffWithoutAnotherAttempt()
    {
        using var backend = new SignalingBackend();
        var coordinator = new DownloadTransferCoordinator(
            backend,
            new DownloadRetryPolicy(
                maximumAttempts: 5,
                baseDelay: TimeSpan.FromMinutes(1)),
            TimeProvider.System,
            NullLogger<DownloadTransferCoordinator>.Instance);
        using var cancellation = new CancellationTokenSource();

        var transfer = coordinator.TransferAsync(
            CreateRequest("https://primary.invalid/media"),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            cancellation.Token);
        await backend.FirstAttempt.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Equal(1, backend.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, (int)DownloadTransferFailureKind.ExpiredAddress)]
    [InlineData(HttpStatusCode.NotFound, (int)DownloadTransferFailureKind.ExpiredAddress)]
    [InlineData(HttpStatusCode.RequestTimeout, (int)DownloadTransferFailureKind.TransientNetwork)]
    [InlineData(HttpStatusCode.TooManyRequests, (int)DownloadTransferFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, (int)DownloadTransferFailureKind.TransientNetwork)]
    public void BuiltinBackendClassifiesHttpFailures(
        HttpStatusCode statusCode,
        int expectedValue)
    {
        var exception = new HttpRequestException(
            "Sanitized test failure.",
            inner: null,
            statusCode);

        var result = BuiltinTransferBackend.ClassifyFailure(
            exception,
            reportedCanceled: false);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Equal((DownloadTransferFailureKind)expectedValue, result.FailureKind);
    }

    [Fact]
    public void BuiltinBackendClassifiesTransportFailureWithoutStatusAsTransient()
    {
        var result = BuiltinTransferBackend.ClassifyFailure(
            new HttpRequestException("Sanitized transport failure."),
            reportedCanceled: false);

        Assert.Equal(DownloadTransferFailureKind.TransientNetwork, result.FailureKind);
    }

    [Theory]
    [MemberData(nameof(TransientDownloaderFailures))]
    public void BuiltinBackendClassifiesDownloaderTransportFailuresAsTransient(
        Exception exception)
    {
        var result = BuiltinTransferBackend.ClassifyFailure(
            new AggregateException(exception),
            reportedCanceled: false);

        Assert.Equal(DownloadTransferFailureKind.TransientNetwork, result.FailureKind);
    }

    public static TheoryData<Exception> TransientDownloaderFailures =>
        new()
        {
            new IncompleteDownloadException("Sanitized incomplete transfer."),
            new HttpIOException(
                HttpRequestError.ResponseEnded,
                "Sanitized response failure."),
            new SocketException((int)SocketError.ConnectionReset)
        };

    [Fact]
    public void BuiltinBackendClassifiesDiskFailuresWithoutRetry()
    {
        var result = BuiltinTransferBackend.ClassifyFailure(
            new UnauthorizedAccessException(),
            reportedCanceled: false);

        Assert.Equal(DownloadTransferFailureKind.Disk, result.FailureKind);
    }

    [Fact]
    public void BuiltinBackendClassifiesCertificateAuthenticationFailures()
    {
        var exception = new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException(
                "The remote certificate is invalid because the certificate chain is untrusted."));

        var result = BuiltinTransferBackend.ClassifyFailure(
            exception,
            reportedCanceled: false);

        Assert.Equal(DownloadTransferFailureKind.Tls, result.FailureKind);
        Assert.Equal("download.transfer.tls.untrusted", result.ErrorCode);
    }

    [Theory]
    [InlineData("SSL/TLS handshake failure: unknown CA", "download.transfer.tls.untrusted")]
    [InlineData("certificate has expired", "download.transfer.tls.expired")]
    [InlineData("certificate is not yet valid", "download.transfer.tls.not-yet-valid")]
    [InlineData("certificate hostname does not match", "download.transfer.tls.hostname")]
    [InlineData("certificate chain building failed", "download.transfer.tls.chain")]
    [InlineData("SSL/TLS handshake failure", "download.transfer.tls.handshake")]
    [InlineData("SSL/TLS handshake failure (80090325)", "download.transfer.tls.untrusted")]
    [InlineData("SSL/TLS handshake failure (80090322)", "download.transfer.tls.hostname")]
    public void AriaBackendClassifiesTlsFailuresWithoutExposingRawMessages(
        string errorMessage,
        string expectedCode)
    {
        var result = Aria2TransferFailureClassifier.Classify("1", errorMessage);

        Assert.Equal(DownloadTransferFailureKind.Tls, result.FailureKind);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.DoesNotContain(errorMessage, result.ErrorCode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("22", "HTTP response status was 403", (int)DownloadTransferFailureKind.ExpiredAddress)]
    [InlineData("22", "HTTP response status was 429", (int)DownloadTransferFailureKind.RateLimited)]
    [InlineData("22", "HTTP response status was 503", (int)DownloadTransferFailureKind.TransientNetwork)]
    [InlineData("2", "timeout", (int)DownloadTransferFailureKind.TransientNetwork)]
    [InlineData("8", "resume unsupported", (int)DownloadTransferFailureKind.ResumeRejected)]
    [InlineData("9", "disk full", (int)DownloadTransferFailureKind.Disk)]
    [InlineData("18", "create directory failed", (int)DownloadTransferFailureKind.Disk)]
    [InlineData("19", "name resolution failed", (int)DownloadTransferFailureKind.TransientNetwork)]
    [InlineData("32", "checksum failed", (int)DownloadTransferFailureKind.InvalidMedia)]
    [InlineData("24", "authorization failed", (int)DownloadTransferFailureKind.Permanent)]
    public void AriaBackendClassifiesMachineReadableFailures(
        string errorCode,
        string errorMessage,
        int expectedValue)
    {
        var result = Aria2TransferFailureClassifier.Classify(errorCode, errorMessage);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Equal((DownloadTransferFailureKind)expectedValue, result.FailureKind);
        Assert.Equal($"download.transfer.aria2-{errorCode}", result.ErrorCode);
    }

    [Theory]
    [InlineData("33", "No URI available.", "download.transfer.insecure-redirect")]
    [InlineData("34", "No URI available.", "download.transfer.credentialed-redirect")]
    public void AriaBackendPreservesSecureRedirectRejectionCodes(
        string errorCode,
        string errorMessage,
        string expectedDiagnostic)
    {
        var result = Aria2TransferFailureClassifier.Classify(errorCode, errorMessage);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Equal(DownloadTransferFailureKind.Permanent, result.FailureKind);
        Assert.Equal(expectedDiagnostic, result.ErrorCode);
        Assert.DoesNotContain(errorMessage, result.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AriaBackendDoesNotTreatDigitsInsideAnotherNumberAsHttpStatus()
    {
        var result = Aria2TransferFailureClassifier.Classify(
            "1",
            "Downloaded 1403 bytes before an unknown failure.");

        Assert.Equal(DownloadTransferFailureKind.Permanent, result.FailureKind);
    }

    [Theory]
    [InlineData("rpc-1", false)]
    [InlineData("rpc-empty", false)]
    [InlineData("not-found", true)]
    [InlineData("6", true)]
    [InlineData(null, false)]
    public void AriaBackendClearsIdentityOnlyAfterTaskLevelFailure(
        string? errorCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            Aria2TransferBackend.ShouldClearBackendIdentity(errorCode));
    }

    [Fact]
    public void InvalidArtifactCleanupRemovesMediaAndResumeSidecars()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-invalid-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var media = Path.Combine(directory, "media.tmp");
        File.WriteAllText(media, "invalid");
        File.WriteAllText($"{media}.aria2", "resume");
        File.WriteAllText($"{media}.download", "resume");

        try
        {
            var result = DownloadTransferFileCleanup.DeleteInvalidArtifacts(
                media,
                NullLogger.Instance);

            Assert.True(result.Succeeded);
            Assert.Equal(3, result.AttemptedCount);
            Assert.False(File.Exists(media));
            Assert.False(File.Exists($"{media}.aria2"));
            Assert.False(File.Exists($"{media}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorStopsRetryWhenInvalidArtifactCleanupFails()
    {
        using var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.InvalidMedia,
                "invalid-media"),
            DownloadTransferResult.Succeeded());
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-cleanup-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "blocked-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(
            $"{target}.aria2",
            "resume",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            $"{target}.download",
            "resume",
            TestContext.Current.CancellationToken);

        try
        {
            var request = CreateRequest("https://primary.invalid/media") with
            {
                Directory = directory,
                FileName = Path.GetFileName(target)
            };

            var result = await coordinator.TransferAsync(
                request,
                static _ => Task.FromResult<IReadOnlyList<string>>([]),
                TestContext.Current.CancellationToken);

            Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
            Assert.Equal(DownloadTransferFailureKind.Disk, result.FailureKind);
            Assert.Equal("download.transfer.cleanup-failed", result.ErrorCode);
            Assert.Single(backend.Requests);
            Assert.True(Directory.Exists(target));
            Assert.True(File.Exists($"{target}.aria2"));
            Assert.True(File.Exists($"{target}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DownloadTransferCoordinator CreateCoordinator(
        ITransferBackend backend,
        int maximumAttempts)
    {
        return new DownloadTransferCoordinator(
            backend,
            new DownloadRetryPolicy(maximumAttempts, TimeSpan.Zero),
            TimeProvider.System,
            NullLogger<DownloadTransferCoordinator>.Instance);
    }

    private static DownloadTransferRequest CreateRequest(params string[] addresses)
    {
        return new DownloadTransferRequest(
            new DownKyi.Domain.Downloads.DownloadTaskId("retry-test"),
            BackendIdentity: null,
            addresses,
            Directory: Path.GetTempPath(),
            FileName: "retry-test.tmp",
            ExpectedBytes: 0,
            EnsureActive: static () => { },
            IsPauseRequested: static () => false,
            PublishProgress: static _ => { },
            PersistProgressAsync: static (_, _) => Task.CompletedTask,
            SetBackendIdentityAsync: static (_, _) => Task.CompletedTask,
            SetBuiltinDownloadService: static _ => { },
            CancellationToken.None);
    }

    private sealed class RecordingBackend(
        params DownloadTransferResult[] results) : ITransferBackend
    {
        private readonly Queue<DownloadTransferResult> _results = new(results);

        public List<DownloadTransferRequest> Requests { get; } = [];

        public string Name => "recording";

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(
                _results.Count > 0
                    ? _results.Dequeue()
                    : results[^1]);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CancelingBackend : ITransferBackend
    {
        public int Attempts { get; private set; }

        public string Name => "canceling";

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            Attempts++;
            throw new OperationCanceledException(request.CancellationToken);
        }

        public void Dispose()
        {
        }
    }

    private sealed class SignalingBackend : ITransferBackend
    {
        private readonly TaskCompletionSource _firstAttempt =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstAttempt => _firstAttempt.Task;

        public int Attempts { get; private set; }

        public string Name => "signaling";

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            _firstAttempt.TrySetResult();
            return Task.FromResult(DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.timeout"));
        }

        public void Dispose()
        {
        }
    }

    private sealed class IdentityPublishingBackend : ITransferBackend
    {
        public List<string?> ObservedIdentities { get; } = [];

        public string Name => "identity-publishing";

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<DownloadTransferResult> TransferAsync(
            DownloadTransferRequest request)
        {
            ObservedIdentities.Add(request.BackendIdentity);
            if (ObservedIdentities.Count == 1)
            {
                await request.SetBackendIdentityAsync(
                    "test-backend-id",
                    request.CancellationToken).ConfigureAwait(true);
                return DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.TransientNetwork,
                    "download.transfer.aria2-rpc");
            }

            return DownloadTransferResult.Succeeded();
        }

        public void Dispose()
        {
        }
    }
}
