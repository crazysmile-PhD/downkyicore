using System.Net;
using System.Net.Http;
using System.Reflection;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Tests;

public sealed class Aria2PauseCheckpointTests
{
    [Fact]
    public async Task PauseSignalInterruptsPollingAndWaitsForAriaPausedState()
    {
        var directory = CreateTemporaryDirectory();
        var initialStatusStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialStatus = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseRpcObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCheckpointObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPausedCheckpoint = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseState = 0;
        var tellStatusCountAfterPause = 0;
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task<DownloadTransferResult>? transferTask = null;

        try
        {
            using var settings = new TestSettingsStore();
            var client = new AriaClient(
                "http://localhost",
                6800,
                "test-token",
                async (_, payload, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var request = JObject.Parse(payload);
                    var method = request["method"]?.Value<string>();
                    JToken result;
                    switch (method)
                    {
                        case "aria2.addUri":
                            result = JValue.CreateString("pause-test-gid");
                            break;
                        case "aria2.pause":
                            Interlocked.Exchange(ref pauseState, 1);
                            pauseRpcObserved.TrySetResult();
                            result = JValue.CreateString("pause-test-gid");
                            break;
                        case "aria2.tellStatus" when Volatile.Read(ref pauseState) == 0:
                            initialStatusStarted.TrySetResult();
                            await releaseInitialStatus.Task
                                .WaitAsync(cancellationToken)
                                .ConfigureAwait(false);
                            result = CreateStatus("active");
                            break;
                        case "aria2.tellStatus":
                            if (Interlocked.Increment(ref tellStatusCountAfterPause) == 1)
                            {
                                activeCheckpointObserved.TrySetResult();
                                result = CreateStatus("active");
                                break;
                            }

                            await allowPausedCheckpoint.Task
                                .WaitAsync(cancellationToken)
                                .ConfigureAwait(false);
                            result = CreateStatus("paused");
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unexpected aria2 RPC method '{method}'.");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = "pause-test",
                        result
                    });
                });
            using var probeHandler = new AcceptingHttpMessageHandler();
            using var replacementResolver = AriaDownloadAddressResolver.CreateForTest(
                probeHandler);
            using var backend = new Aria2TransferBackend(
                settings.Store.Current.Network,
                client,
                new AriaRuntimeClientRegistry(),
                new DownloadDiagnosticLogger(
                    NullLogger<DownloadDiagnosticLogger>.Instance),
                new AriaServer(NullLoggerFactory.Instance),
                NullLoggerFactory.Instance,
                NullLogger<Aria2TransferBackend>.Instance,
                ownsAriaServer: false,
                localEndpoint: null);
            ReplaceAddressResolverForTest(backend, replacementResolver);
            var request = new DownloadTransferRequest(
                new DownloadTaskId("aria-pause-checkpoint"),
                BackendIdentity: null,
                Urls: ["https://download.example/media"],
                Directory: directory,
                FileName: "media.tmp",
                ExpectedBytes: 0,
                EnsureActive: static () => { },
                IsPauseRequested: () => pauseRequested.Task.IsCompleted,
                WaitForPauseRequestedAsync: token => pauseRequested.Task.WaitAsync(token),
                PublishProgress: static _ => { },
                PersistProgressAsync: static (_, _) => Task.CompletedTask,
                SetBackendIdentityAsync: static (_, _) => Task.CompletedTask,
                CancellationToken: requestCancellation.Token,
                StagingDirectory: directory);

            transferTask = backend.TransferAsync(request);
            await initialStatusStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            pauseRequested.TrySetResult();
            await pauseRpcObserved.Task
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await activeCheckpointObserved.Task
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.False(transferTask.IsCompleted);

            releaseInitialStatus.TrySetResult();
            allowPausedCheckpoint.TrySetResult();
            var transferResult = await transferTask
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(DownloadTransferOutcome.Paused, transferResult.Outcome);
            Assert.True(tellStatusCountAfterPause >= 2);
        }
        finally
        {
            await requestCancellation.CancelAsync().ConfigureAwait(true);
            releaseInitialStatus.TrySetResult();
            allowPausedCheckpoint.TrySetResult();
            if (transferTask != null)
            {
                try
                {
                    await transferTask.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                }
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static JObject CreateStatus(string status)
    {
        return JObject.FromObject(new
        {
            status,
            totalLength = "1024",
            completedLength = "512",
            downloadSpeed = status == "active" ? "128" : "0",
            files = Array.Empty<object>()
        });
    }

    private static void ReplaceAddressResolverForTest(
        Aria2TransferBackend backend,
        AriaDownloadAddressResolver replacement)
    {
        var field = typeof(Aria2TransferBackend).GetField(
            "_addressResolver",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The aria2 address resolver field is missing.");
        var original = Assert.IsType<AriaDownloadAddressResolver>(field.GetValue(backend));
        field.SetValue(backend, replacement);
        original.Dispose();
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-aria-pause-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class AcceptingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([0])
            });
        }
    }
}
