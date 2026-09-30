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

public sealed class Aria2ResumePreparationTests
{
    [Fact]
    public async Task ResumeUsesAriaTaskAddressWithoutRemotePreflight()
    {
        var directory = CreateTemporaryDirectory();
        var target = Path.Combine(directory, "media.tmp");
        await File.WriteAllBytesAsync(
            target,
            [0, 1, 2, 3],
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var requests = new List<JObject>();
        var controlRequests = new List<string>();
        var tellStatusCount = 0;

        try
        {
            using var settings = new TestSettingsStore();
            var client = new AriaClient(
                "http://localhost",
                6800,
                "test-token",
                (_, payload, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var request = JObject.Parse(payload);
                    requests.Add(request);
                    var method = request["method"]?.Value<string>();
                    JToken result;
                    switch (method)
                    {
                        case "aria2.tellStatus"
                            when Interlocked.Increment(ref tellStatusCount) == 1:
                            result = CreateStatus("paused", target);
                            break;
                        case "aria2.tellStatus":
                            result = CreateStatus("complete", target);
                            break;
                        case "aria2.getUris":
                            controlRequests.Add(method);
                            result = JArray.FromObject(new[]
                            {
                                new
                                {
                                    status = "used",
                                    uri = "https://download.example/current-address"
                                }
                            });
                            break;
                        case "aria2.changeOption":
                            controlRequests.Add(method);
                            result = JValue.CreateString("OK");
                            break;
                        case "aria2.unpause":
                            controlRequests.Add(method);
                            result = JValue.CreateString("resume-test-gid");
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unexpected aria2 RPC method '{method}'.");
                    }

                    return Task.FromResult<string?>(JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = "resume-test",
                        result
                    }));
                });
            using var probeHandler = new FailOnProbeHandler();
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
                new DownloadTaskId("aria-resume-preparation"),
                BackendIdentity: "resume-test-gid",
                Urls: ["https://request.example/original-address"],
                Directory: directory,
                FileName: Path.GetFileName(target),
                ExpectedBytes: 4,
                EnsureActive: static () => { },
                IsPauseRequested: static () => false,
                WaitForPauseRequestedAsync: static token =>
                    Task.Delay(Timeout.InfiniteTimeSpan, token),
                PublishProgress: static _ => { },
                PersistProgressAsync: static (_, _) => Task.CompletedTask,
                SetBackendIdentityAsync: static (_, _) => Task.CompletedTask,
                CancellationToken: TestContext.Current.CancellationToken,
                StagingDirectory: directory);

            var transferResult = await backend.TransferAsync(request)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(0, probeHandler.RequestCount);
            Assert.Equal(
                ["aria2.getUris", "aria2.changeOption", "aria2.unpause"],
                controlRequests);
            var changeOption = requests.Single(request =>
                string.Equals(
                    request["method"]?.Value<string>(),
                    "aria2.changeOption",
                    StringComparison.Ordinal));
            var options = Assert.IsType<JObject>(
                Assert.IsType<JArray>(changeOption["params"])[2]);
            Assert.Equal(
                settings.Store.Current.Network.UserAgent,
                options["user-agent"]?.Value<string>());
            Assert.Equal(DownloadTransferOutcome.Succeeded, transferResult.Outcome);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JObject CreateStatus(string status, string target)
    {
        return JObject.FromObject(new
        {
            status,
            totalLength = "4",
            completedLength = status == "complete" ? "4" : "2",
            downloadSpeed = "0",
            files = new[] { new { path = target } }
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
            $"downkyi-aria-resume-preparation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FailOnProbeHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException(
                "An existing aria2 task must not perform a remote preflight.");
        }
    }
}
