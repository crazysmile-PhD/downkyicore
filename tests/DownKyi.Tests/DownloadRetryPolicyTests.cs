using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadRetryPolicyTests
{
    [Fact]
    public void TransferFailureDiagnosticUsesInvariantIntegerMilliseconds()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            var diagnostic = DownloadTransferCoordinator.FormatFailureDiagnostic(
                "built-in",
                attempt: 1,
                maximumAttempts: 5,
                failureKind: DownloadTransferFailureKind.TransientNetwork,
                errorCode: "download.transfer.network",
                nextAction: DownloadRetryAction.RetrySameAddress,
                delay: TimeSpan.FromMilliseconds(500));

            Assert.Equal(
                "Download transfer attempt failed; backend=built-in; attempt=1/5; failure=TransientNetwork; error=download.transfer.network; next=RetrySameAddress; delayMilliseconds=500",
                diagnostic);
            Assert.DoesNotContain("٫", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task CoordinatorRetriesEachTransientSourceBeforeStopping()
    {
        var directory = CreateTemporaryDirectory("transient-sources");
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.network"));
        try
        {
            var result = await CreateCoordinator(backend, maximumAttempts: 5).TransferAsync(
                CreateRequestAt(
                    directory,
                    "media.tmp",
                    "https://primary.invalid/media",
                    "https://backup-1.invalid/media",
                    "https://backup-2.invalid/media"),
                static _ => Task.FromResult<IReadOnlyList<string>>([]),
                TestContext.Current.CancellationToken);

            Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
            Assert.Equal(6, backend.Requests.Count);
            Assert.Equal(backend.Requests[0].Urls, backend.Requests[1].Urls);
            Assert.Equal(backend.Requests[2].Urls, backend.Requests[3].Urls);
            Assert.Equal(backend.Requests[4].Urls, backend.Requests[5].Urls);
            Assert.Equal(
                3,
                backend.Requests.SelectMany(request => request.Urls).Distinct().Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorTriesNextAddressAfterCandidateRejection()
    {
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.CandidateRejected,
                "download.transfer.insecure-address"),
            DownloadTransferResult.Succeeded());
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);
        var refreshCount = 0;

        var result = await coordinator.TransferAsync(
            CreateRequest(
                "https://primary.invalid/media",
                "https://backup.invalid/media"),
            _ =>
            {
                refreshCount++;
                return Task.FromResult<IReadOnlyList<string>>([]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, backend.Requests.Count);
        Assert.Equal("https://primary.invalid/media", Assert.Single(backend.Requests[0].Urls));
        Assert.Equal("https://backup.invalid/media", Assert.Single(backend.Requests[1].Urls));
        Assert.Equal(0, refreshCount);
    }

    [Fact]
    public async Task CoordinatorStopsAfterAllCandidatesAreRejected()
    {
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.CandidateRejected,
                "download.transfer.insecure-address"),
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.CandidateRejected,
                "download.transfer.tls.hostname"));
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);
        var refreshCount = 0;

        var result = await coordinator.TransferAsync(
            CreateRequest(
                "https://primary.invalid/media",
                "https://backup.invalid/media"),
            _ =>
            {
                refreshCount++;
                return Task.FromResult<IReadOnlyList<string>>([]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Equal(DownloadTransferFailureKind.CandidateRejected, result.FailureKind);
        Assert.Equal("download.transfer.tls.hostname", result.ErrorCode);
        Assert.Equal(2, backend.Requests.Count);
        Assert.Equal(0, refreshCount);
    }

    [Fact]
    public async Task CoordinatorRetriesTransientFailureOnSameAddressBeforeMoving()
    {
        var backend = new RecordingBackend(
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
    public async Task CoordinatorDeletesInvalidMediaAfterTerminalFailure()
    {
        var directory = CreateTemporaryDirectory("terminal-invalid-media");
        const string fileName = "media.tmp";
        var target = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(
            target,
            "invalid",
            TestContext.Current.CancellationToken);
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.InvalidMedia,
                "download.transfer.invalid-media"));

        try
        {
            var result = await CreateCoordinator(backend, maximumAttempts: 5).TransferAsync(
                CreateRequestAt(
                    directory,
                    fileName,
                    "https://primary.invalid/media"),
                static _ => Task.FromResult<IReadOnlyList<string>>([]),
                TestContext.Current.CancellationToken);

            Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
            Assert.Equal(DownloadTransferFailureKind.InvalidMedia, result.FailureKind);
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorClearsPartialStateBeforeContactingBackupSource()
    {
        var directory = CreateTemporaryDirectory("source-switch");
        const string fileName = "media.tmp";
        var target = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(
            target,
            "partial",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            $"{target}.download",
            "resume",
            TestContext.Current.CancellationToken);
        var backend = new InspectingBackend(
            (attempt, request) =>
            {
                if (attempt <= 2)
                {
                    Assert.True(File.Exists(target));
                    return DownloadTransferResult.Failed(
                        DownloadTransferFailureKind.TransientNetwork,
                        "download.transfer.timeout");
                }

                Assert.False(File.Exists(target));
                Assert.False(File.Exists($"{target}.download"));
                return DownloadTransferResult.Succeeded();
            });

        try
        {
            var result = await CreateCoordinator(backend, maximumAttempts: 5).TransferAsync(
                CreateRequestAt(
                    directory,
                    fileName,
                    "https://primary.invalid/media",
                    "https://backup.invalid/media"),
                static _ => Task.FromResult<IReadOnlyList<string>>([]),
                TestContext.Current.CancellationToken);

            Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
            Assert.Equal(3, backend.Requests.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorPreservesResumeStateWhenRefreshReturnsSameSource()
    {
        var directory = CreateTemporaryDirectory("same-source");
        const string fileName = "media.tmp";
        var target = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(
            target,
            "partial",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            $"{target}.download",
            "resume",
            TestContext.Current.CancellationToken);
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ExpiredAddress,
                "download.transfer.http-403"),
            DownloadTransferResult.Succeeded());

        try
        {
            var result = await CreateCoordinator(backend, maximumAttempts: 5).TransferAsync(
                CreateRequestAt(
                    directory,
                    fileName,
                    "https://primary.invalid/media"),
                static _ => Task.FromResult<IReadOnlyList<string>>(
                    ["  https://primary.invalid/media  "]),
                TestContext.Current.CancellationToken);

            Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
            Assert.True(File.Exists(target));
            Assert.True(File.Exists($"{target}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorFailsClosedWhenSourceChangeCleanupCannotComplete()
    {
        var directory = CreateTemporaryDirectory("cleanup-failure");
        const string fileName = "media.tmp";
        var target = Path.Combine(directory, fileName);
        Directory.CreateDirectory($"{target}.download");
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.timeout"));
        try
        {
            var result = await CreateCoordinator(backend, maximumAttempts: 5).TransferAsync(
                CreateRequestAt(
                    directory,
                    fileName,
                    "https://primary.invalid/media",
                    "https://backup.invalid/media"),
                static _ => Task.FromResult<IReadOnlyList<string>>([]),
                TestContext.Current.CancellationToken);

            Assert.Equal(DownloadTransferFailureKind.Disk, result.FailureKind);
            Assert.Equal("download.transfer.source-change-cleanup", result.ErrorCode);
            Assert.Equal(2, backend.Requests.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuiltinBackendRejectsMultipleAddressesBeforeStartingDownloader()
    {
        using var settings = new TestSettingsStore();
        var backend = new BuiltinTransferBackend(
            settings.Store,
            new DownloadDiagnosticLogger(
                NullLogger<DownloadDiagnosticLogger>.Instance),
            NullLogger<BuiltinTransferBackend>.Instance);

        var result = await backend.TransferAsync(CreateRequest(
            "https://primary.invalid/media",
            "https://backup.invalid/media"));

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Equal(DownloadTransferFailureKind.Permanent, result.FailureKind);
        Assert.Equal("download.transfer.single-address-required", result.ErrorCode);
    }

    [Fact]
    public async Task BuiltinBackendClassifiesAddressPreflightRejectionAsCandidateRejected()
    {
        var directory = CreateTemporaryDirectory("builtin-candidate-rejected");
        using var settings = new TestSettingsStore();
        var backend = new BuiltinTransferBackend(
            settings.Store,
            new DownloadDiagnosticLogger(
                NullLogger<DownloadDiagnosticLogger>.Instance),
            NullLogger<BuiltinTransferBackend>.Instance);

        try
        {
            var result = await backend.TransferAsync(CreateRequestAt(
                directory,
                "media.tmp",
                "http://download.invalid/media"));

            Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
            Assert.Equal(DownloadTransferFailureKind.CandidateRejected, result.FailureKind);
            Assert.Equal("download.transfer.insecure-address", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorRetriesTypedAddressProbeTimeoutBeforeMovingToBackup()
    {
        var directory = CreateTemporaryDirectory("address-probe-timeout");
        using var settings = new TestSettingsStore();
        var probedAddresses = new List<string>();
        var backend = new BuiltinTransferBackend(
            settings.Store,
            new DownloadDiagnosticLogger(
                NullLogger<DownloadDiagnosticLogger>.Instance),
            NullLogger<BuiltinTransferBackend>.Instance,
            _ => new StubAddressResolver(
                new DownloadAddressTimedOut(),
                probedAddresses));
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);

        try
        {
            var result = await coordinator.TransferAsync(
                CreateRequestAt(
                    directory,
                    "media.tmp",
                    "https://primary.invalid/media",
                    "https://backup.invalid/media"),
                static _ => Task.FromResult<IReadOnlyList<string>>([]),
                TestContext.Current.CancellationToken);

            Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
            Assert.Equal(DownloadTransferFailureKind.TransientNetwork, result.FailureKind);
            Assert.Equal("download.transfer.timeout", result.ErrorCode);
            Assert.Equal(
                [
                    "https://primary.invalid/media",
                    "https://primary.invalid/media",
                    "https://backup.invalid/media",
                    "https://backup.invalid/media"
                ],
                probedAddresses);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuiltinBackendRecoversCompletedTargetBeforeResolvingAddress()
    {
        using var settings = new TestSettingsStore();
        var backend = new BuiltinTransferBackend(
            settings.Store,
            new DownloadDiagnosticLogger(
                NullLogger<DownloadDiagnosticLogger>.Instance),
            NullLogger<BuiltinTransferBackend>.Instance);
        var directory = CreateTemporaryDirectory("completed-target-recovery");
        const string fileName = "media.bin";
        var target = Path.Combine(directory, fileName);
        var payload = new byte[] { 1, 2, 3, 4 };
        try
        {
            await File.WriteAllBytesAsync(
                target,
                payload,
                TestContext.Current.CancellationToken);
            var request = CreateRequestAt(
                directory,
                fileName,
                "https://unresolvable.invalid/media") with
            {
                ExpectedBytes = payload.Length,
                CancellationToken = TestContext.Current.CancellationToken
            };

            var result = await backend.TransferAsync(request);

            Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("sidecar")]
    [InlineData("error-payload")]
    public async Task BuiltinBackendRejectsInvalidCompletedTargetBeforeResolvingAddress(
        string invalidKind)
    {
        using var settings = new TestSettingsStore();
        var backend = new BuiltinTransferBackend(
            settings.Store,
            new DownloadDiagnosticLogger(
                NullLogger<DownloadDiagnosticLogger>.Instance),
            NullLogger<BuiltinTransferBackend>.Instance);
        var directory = CreateTemporaryDirectory("invalid-completed-target");
        const string fileName = "media.bin";
        var target = Path.Combine(directory, fileName);
        var payload = invalidKind == "error-payload"
            ? "<html>expired media response</html>"u8.ToArray()
            : new byte[] { 1, 2, 3, 4 };
        try
        {
            await File.WriteAllBytesAsync(
                target,
                payload,
                TestContext.Current.CancellationToken);
            if (invalidKind == "sidecar")
            {
                await File.WriteAllTextAsync(
                    $"{target}.download",
                    "unfinished",
                    TestContext.Current.CancellationToken);
            }

            var request = CreateRequestAt(
                directory,
                fileName,
                "https://unresolvable.invalid/media") with
            {
                ExpectedBytes = payload.Length,
                CancellationToken = TestContext.Current.CancellationToken
            };

            var result = await backend.TransferAsync(request);

            Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
            Assert.Equal(DownloadTransferFailureKind.ResumeRejected, result.FailureKind);
            Assert.Equal("download.transfer.resume-rejected", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorRefreshesExpiredAddressesOnlyOnce()
    {
        var backend = new RecordingBackend(
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
        var backend = new RecordingBackend(
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
        var backend = new RecordingBackend(
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
        var backend = new RecordingBackend(
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
    public async Task CoordinatorDoesNotRetryPermanentFailureOrTryBackupAddresses()
    {
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.permanent"));
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);

        var result = await coordinator.TransferAsync(
            CreateRequest("https://primary.invalid/media", "https://backup.invalid/media"),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        Assert.Equal(DownloadTransferFailureKind.Permanent, result.FailureKind);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task CoordinatorRetriesCleanedResumeRejectionOnceBeforeMoving()
    {
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ResumeRejected,
                "download.transfer.resume-rejected"),
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ResumeRejected,
                "download.transfer.resume-rejected"),
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
    public async Task CoordinatorDeletesRejectedPartialBeforeRestartingSameAddress()
    {
        var directory = CreateTemporaryDirectory("resume-overlap-restart");
        const string fileName = "media.tmp";
        var partial = Path.Combine(directory, $"{fileName}.download");
        await File.WriteAllTextAsync(
            partial,
            "rejected partial bytes",
            TestContext.Current.CancellationToken);
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ResumeRejected,
                "download.transfer.resume-rejected"),
            DownloadTransferResult.Succeeded());

        try
        {
            var result = await CreateCoordinator(backend, maximumAttempts: 2).TransferAsync(
                CreateRequestAt(
                    directory,
                    fileName,
                    "https://primary.invalid/media"),
                static _ => Task.FromResult<IReadOnlyList<string>>([]),
                TestContext.Current.CancellationToken);

            Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
            Assert.Equal(2, backend.Requests.Count);
            Assert.Equal(backend.Requests[0].Urls, backend.Requests[1].Urls);
            Assert.False(File.Exists(partial));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorRetriesCleanedResumeRejectionAfterEarlierTransientAttempt()
    {
        var backend = new RecordingBackend(
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.timeout"),
            DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ResumeRejected,
                "download.transfer.resume-rejected"),
            DownloadTransferResult.Succeeded());
        var coordinator = CreateCoordinator(backend, maximumAttempts: 5);

        var result = await coordinator.TransferAsync(
            CreateRequest("https://primary.invalid/media"),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, backend.Requests.Count);
        Assert.All(
            backend.Requests,
            request => Assert.Equal("https://primary.invalid/media", Assert.Single(request.Urls)));
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
            attemptsForAddress: 1,
            hasNextAddress: true,
            canRefreshAddresses: true);

        Assert.Equal(DownloadRetryAction.RetrySameAddress, decision.Action);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.Delay);
    }

    [Fact]
    public async Task CoordinatorDoesNotSwallowCancellation()
    {
        var backend = new CancelingBackend();
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
        var backend = new SignalingBackend();
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
            TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Equal(1, backend.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, (int)DownloadTransferFailureKind.Permanent, "download.transfer.http-401")]
    [InlineData(HttpStatusCode.Forbidden, (int)DownloadTransferFailureKind.ExpiredAddress, "download.transfer.http-403")]
    [InlineData(HttpStatusCode.NotFound, (int)DownloadTransferFailureKind.ExpiredAddress, "download.transfer.http-404")]
    [InlineData(HttpStatusCode.RequestTimeout, (int)DownloadTransferFailureKind.TransientNetwork, "download.transfer.http-408")]
    [InlineData(HttpStatusCode.TooManyRequests, (int)DownloadTransferFailureKind.RateLimited, "download.transfer.http-429")]
    [InlineData(HttpStatusCode.InternalServerError, (int)DownloadTransferFailureKind.TransientNetwork, "download.transfer.http-server-error")]
    [InlineData(HttpStatusCode.BadRequest, (int)DownloadTransferFailureKind.Permanent, "download.transfer.http-client-error")]
    public void BuiltinBackendClassifiesHttpFailures(
        HttpStatusCode statusCode,
        int expectedValue,
        string expectedCode)
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
        Assert.Equal(expectedCode, result.ErrorCode);
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
    [MemberData(nameof(TransientRangeFailures))]
    public void BuiltinBackendClassifiesRangeTransportFailuresAsTransient(
        Exception exception)
    {
        var result = BuiltinTransferBackend.ClassifyFailure(
            new AggregateException(exception),
            reportedCanceled: false);

        Assert.Equal(DownloadTransferFailureKind.TransientNetwork, result.FailureKind);
    }

    public static TheoryData<Exception> TransientRangeFailures =>
        new()
        {
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
        var decision = new DownloadRetryPolicy().Decide(
            result,
            attemptsForAddress: 1,
            hasNextAddress: true,
            canRefreshAddresses: true);
        Assert.Equal(DownloadRetryAction.Stop, decision.Action);
    }

    [Fact]
    public void BuiltinBackendTreatsSecureConnectionInterruptionAsRetryable()
    {
        var exception = new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established.",
            new AuthenticationException(
                "Authentication failed because the transport stream ended."),
            statusCode: null);

        var result = BuiltinTransferBackend.ClassifyFailure(
            exception,
            reportedCanceled: false);

        Assert.Equal(DownloadTransferFailureKind.TransientNetwork, result.FailureKind);
        var decision = new DownloadRetryPolicy().Decide(
            result,
            attemptsForAddress: 1,
            hasNextAddress: true,
            canRefreshAddresses: true);
        Assert.Equal(DownloadRetryAction.RetrySameAddress, decision.Action);
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
        File.WriteAllText($"{media}.download", "resume");

        try
        {
            var result = DownloadTransferFileCleanup.DeleteInvalidArtifacts(
                media,
                directory,
                NullLogger.Instance);

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.AttemptedCount);
            Assert.False(File.Exists(media));
            Assert.False(File.Exists($"{media}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidArtifactCleanupRefusesPublishedPathOutsideTaskStaging()
    {
        var directory = CreateTemporaryDirectory("cleanup-boundary");
        var staging = Path.Combine(directory, ".downkyi", "staging", "session", "task");
        Directory.CreateDirectory(staging);
        var published = Path.Combine(directory, "output.mp4");
        File.WriteAllBytes(published, [91, 0, 255, 17]);
        File.WriteAllBytes(published + ".legacy-partial", [4]);
        try
        {
            var result = DownloadTransferFileCleanup.DeleteInvalidArtifacts(
                published, staging, NullLogger.Instance);

            Assert.False(result.Succeeded);
            Assert.Equal(new byte[] { 91, 0, 255, 17 }, File.ReadAllBytes(published));
            Assert.True(File.Exists(published + ".legacy-partial"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorStopsRetryWhenInvalidArtifactCleanupFails()
    {
        var backend = new RecordingBackend(
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
            $"{target}.legacy-partial",
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
                StagingDirectory = directory,
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
            Assert.True(File.Exists($"{target}.legacy-partial"));
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
        return CreateRequestAt(
            Path.GetTempPath(),
            "retry-test.tmp",
            addresses);
    }

    private static DownloadTransferRequest CreateRequestAt(
        string directory,
        string fileName,
        params string[] addresses)
    {
        return new DownloadTransferRequest(
            new DownKyi.Domain.Downloads.DownloadTaskId("retry-test"),
            addresses,
            Directory: directory,
            FileName: fileName,
            ExpectedBytes: 0,
            EnsureActive: static () => { },
            IsPauseRequested: static () => false,
            PublishProgress: static _ => { },
            PersistProgressAsync: static (_, _) => Task.CompletedTask,
            CancellationToken.None,
            directory);
    }

    private static string CreateTemporaryDirectory(string purpose)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingBackend(
        params DownloadTransferResult[] results) : ITransferBackend
    {
        private readonly Queue<DownloadTransferResult> _results = new(results);

        public List<DownloadTransferRequest> Requests { get; } = [];

        public string Name => "recording";

        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(
                _results.Count > 0
                    ? _results.Dequeue()
                    : results[^1]);
        }

    }

    private sealed class StubAddressResolver(
        DownloadAddressResolution result,
        ICollection<string> probedAddresses) : IDownloadAddressResolver
    {
        public Task<DownloadAddressResolution> ResolveAsync(
            string address,
            string userAgent,
            string? credentials,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probedAddresses.Add(address);
            return Task.FromResult(result);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CancelingBackend : ITransferBackend
    {
        public int Attempts { get; private set; }

        public string Name => "canceling";

        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            Attempts++;
            throw new OperationCanceledException(request.CancellationToken);
        }

    }

    private sealed class SignalingBackend : ITransferBackend
    {
        private readonly TaskCompletionSource _firstAttempt =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstAttempt => _firstAttempt.Task;

        public int Attempts { get; private set; }

        public string Name => "signaling";

        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            _firstAttempt.TrySetResult();
            return Task.FromResult(DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.timeout"));
        }

    }

    private sealed class InspectingBackend(
        Func<int, DownloadTransferRequest, DownloadTransferResult> transfer) : ITransferBackend
    {
        private int _attempt;

        public List<DownloadTransferRequest> Requests { get; } = [];

        public string Name => "inspecting";

        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(transfer(++_attempt, request));
        }

    }
}
