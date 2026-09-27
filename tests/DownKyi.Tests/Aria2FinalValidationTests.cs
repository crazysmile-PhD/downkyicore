using System.Net;
using System.Net.Http;
using System.Reflection;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Tests;

public sealed class Aria2FinalValidationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("sidecar")]
    [InlineData("error-payload")]
    public async Task AriaCompleteDoesNotSucceedWhenFinalFileFailsSharedValidation(
        string artifact)
    {
        var directory = CreateTemporaryDirectory();
        var target = Path.Combine(directory, "media.tmp");
        PrepareArtifact(artifact, target);

        try
        {
            var result = await TransferCompletedAriaTaskAsync(directory, target)
                .ConfigureAwait(true);

            Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
            Assert.Equal(DownloadTransferFailureKind.InvalidMedia, result.FailureKind);
            Assert.Equal("download.transfer.invalid-media", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AriaCompleteSucceedsWhenFinalFilePassesSharedValidation()
    {
        var directory = CreateTemporaryDirectory();
        var target = Path.Combine(directory, "media.tmp");
        await File.WriteAllBytesAsync(
            target,
            [0, 1, 2, 3],
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        try
        {
            var result = await TransferCompletedAriaTaskAsync(directory, target)
                .ConfigureAwait(true);

            Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AriaTransferDisablesBackendOwnedRetryAndResumeFallback()
    {
        var directory = CreateTemporaryDirectory();
        var target = Path.Combine(directory, "media.tmp");
        await File.WriteAllBytesAsync(
            target,
            [0, 1, 2, 3],
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        JObject? addUriRequest = null;

        try
        {
            var result = await TransferCompletedAriaTaskAsync(
                directory,
                target,
                request =>
                {
                    if (request["method"]?.Value<string>() == "aria2.addUri")
                    {
                        addUriRequest = request;
                    }
                }).ConfigureAwait(true);

            Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);
            var parameters = Assert.IsType<JArray>(addUriRequest?["params"]);
            var options = Assert.IsType<JObject>(parameters[2]);
            Assert.Equal("1", options["max-tries"]?.Value<string>());
            Assert.Equal("0", options["retry-wait"]?.Value<string>());
            Assert.Equal("false", options["always-resume"]?.Value<string>());
            Assert.Equal("0", options["max-resume-failure-tries"]?.Value<string>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AriaBackendRejectsMultipleAddressesBeforeRpc()
    {
        var directory = CreateTemporaryDirectory();
        var target = Path.Combine(directory, "media.tmp");
        var rpcRequestCount = 0;

        try
        {
            var result = await TransferCompletedAriaTaskAsync(
                directory,
                target,
                _ => rpcRequestCount++,
                [
                    "https://primary.example/media",
                    "https://backup.example/media"
                ]).ConfigureAwait(true);

            Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
            Assert.Equal(DownloadTransferFailureKind.Permanent, result.FailureKind);
            Assert.Equal("download.transfer.single-address-required", result.ErrorCode);
            Assert.Equal(0, rpcRequestCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<DownloadTransferResult> TransferCompletedAriaTaskAsync(
        string directory,
        string target,
        Action<JObject>? observeRequest = null,
        IReadOnlyList<string>? urls = null)
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
                observeRequest?.Invoke(request);
                JToken result = request["method"]?.Value<string>() switch
                {
                    "aria2.addUri" => JValue.CreateString("test-gid"),
                    "aria2.tellStatus" => JObject.FromObject(new
                    {
                        status = "complete",
                        totalLength = "4",
                        completedLength = "4",
                        downloadSpeed = "0",
                        files = new[] { new { path = target } }
                    }),
                    _ => throw new InvalidOperationException("Unexpected aria2 RPC method.")
                };
                return Task.FromResult<string?>(JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    id = "test",
                    result
                }));
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
            new DownloadTaskId("aria-final-validation"),
            BackendIdentity: null,
            Urls: urls ?? ["https://download.example/media"],
            Directory: directory,
            FileName: Path.GetFileName(target),
            ExpectedBytes: 0,
            EnsureActive: static () => { },
            IsPauseRequested: static () => false,
            PublishProgress: static _ => { },
            PersistProgressAsync: static (_, _) => Task.CompletedTask,
            SetBackendIdentityAsync: static (_, _) => Task.CompletedTask,
            SetBuiltinDownloadService: static _ => { },
            CancellationToken: TestContext.Current.CancellationToken,
            StagingDirectory: directory);

        return await backend.TransferAsync(request).ConfigureAwait(true);
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

    private static void PrepareArtifact(string artifact, string target)
    {
        switch (artifact)
        {
            case "missing":
                break;
            case "empty":
                File.WriteAllBytes(target, []);
                break;
            case "sidecar":
                File.WriteAllBytes(target, [0, 1, 2, 3]);
                File.WriteAllText($"{target}.aria2", "unfinished");
                break;
            case "error-payload":
                File.WriteAllText(target, "<html>upstream error</html>");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(artifact), artifact, null);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-aria-final-validation-{Guid.NewGuid():N}");
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
