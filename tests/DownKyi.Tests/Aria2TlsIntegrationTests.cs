using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Services.Download;
using DownKyi.TestInfrastructure;

namespace DownKyi.Tests;

[Collection("Aria2 packaged integration")]
public sealed partial class Aria2TlsIntegrationTests
{
    private const int ExpectedReportCaseCount = 1;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    [Trait("Category", "Aria2TlsIntegration")]
    [Trait("Aria2TlsFamily", "trusted-transfer")]
    public async Task PackagedAria2CompletesTrustedSplitDownload()
    {
        await RunPackagedCaseAsync(
            "trusted-split",
            context => RunTrustedSplitDownloadAsync(
                context.Runtime,
                context.TrustedCertificate,
                context.Payload,
                context.Results,
                context.LoopbackCleanupFailures,
                context.CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Aria2TlsIntegration")]
    [Trait("Aria2TlsFamily", "connect-proxy")]
    public async Task PackagedAria2CompletesTrustedConnectProxyDownload()
    {
        await RunPackagedCaseAsync(
            "trusted-local-connect-proxy",
            context => RunTrustedConnectProxyDownloadAsync(
                context.Runtime,
                context.TrustedCertificate,
                context.Payload,
                context.Results,
                context.LoopbackCleanupFailures,
                context.CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Aria2TlsIntegration")]
    [Trait("Aria2TlsFamily", "connect-proxy")]
    public async Task PackagedAria2RejectsUntrustedConnectProxyInterception()
    {
        await RunPackagedCaseAsync(
            "proxy-untrusted-interception",
            async context =>
            {
                using var unknownAuthority = new TestCertificateAuthority(
                    $"DownKyi Unknown TLS Test {Guid.NewGuid():N}");
                using var unknownCertificate = unknownAuthority.IssueServerCertificate();
                await RunProxyInterceptionRejectedAsync(
                    context.Runtime,
                    unknownCertificate,
                    context.Payload,
                    context.Results,
                    context.CancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Aria2TlsIntegration")]
    [Trait("Aria2TlsFamily", "trusted-resume")]
    public async Task PackagedAria2CompletesTrustedResume()
    {
        await RunPackagedCaseAsync(
            "trusted-resume",
            context => RunTrustedResumeAsync(
                context.Runtime,
                context.TrustedCertificate,
                context.Payload,
                context.Results,
                context.LoopbackCleanupFailures,
                context.CancellationToken)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("unknown-ca")]
    [InlineData("self-signed")]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("hostname-mismatch")]
    [InlineData("missing-san-wrong-common-name")]
    [InlineData("incomplete-chain")]
    [Trait("Category", "Aria2TlsIntegration")]
    [Trait("Aria2TlsFamily", "certificate-rejection")]
    public async Task PackagedAria2RejectsInvalidCertificate(string caseName)
    {
        await RunPackagedCaseAsync(
            caseName,
            async context =>
            {
                using var unknownAuthority = new TestCertificateAuthority(
                    $"DownKyi Unknown TLS Test {Guid.NewGuid():N}");
                using var intermediateCertificate = context.TrustedAuthority
                    .IssueIntermediateCertificate("DownKyi Test Intermediate");
                using var certificate = CreateRejectedCertificate(
                    caseName,
                    context.TrustedAuthority,
                    unknownAuthority,
                    intermediateCertificate);
                await RunRejectedCertificateAsync(
                    context.Runtime,
                    caseName,
                    certificate,
                    context.Payload,
                    GetAcceptedCertificateErrorCodes(caseName),
                    context.Results,
                    context.LoopbackCleanupFailures,
                    context.CancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Aria2TlsIntegration")]
    [Trait("Aria2TlsFamily", "trust-transition")]
    public async Task PackagedAria2RevalidatesCertificateAfterRedirect()
    {
        await RunPackagedCaseAsync(
            "trusted-redirect-to-untrusted",
            async context =>
            {
                using var unknownAuthority = new TestCertificateAuthority(
                    $"DownKyi Unknown TLS Test {Guid.NewGuid():N}");
                using var unknownCertificate = unknownAuthority.IssueServerCertificate();
                await RunRedirectToUntrustedAsync(
                    context.Runtime,
                    context.TrustedCertificate,
                    unknownCertificate,
                    context.Payload,
                    context.Results,
                    context.LoopbackCleanupFailures,
                    context.CancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Aria2TlsIntegration")]
    [Trait("Aria2TlsFamily", "trust-transition")]
    public async Task PackagedAria2RevalidatesCertificateAfterApplicationRetry()
    {
        await RunPackagedCaseAsync(
            "application-retry-resumes-to-untrusted",
            async context =>
            {
                using var unknownAuthority = new TestCertificateAuthority(
                    $"DownKyi Unknown TLS Test {Guid.NewGuid():N}");
                using var unknownCertificate = unknownAuthority.IssueServerCertificate();
                await RunResumeToUntrustedAsync(
                    context.Runtime,
                    context.TrustedCertificate,
                    unknownCertificate,
                    context.Payload,
                    context.Results,
                    context.LoopbackCleanupFailures,
                    context.CancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(true);
    }

    internal static async Task RunRpcLifecycleCaseAsync()
    {
        await RunPackagedCaseAsync(
            "rpc-add-query-remove",
            context => RunRpcRemovalAsync(
                context.Runtime,
                context.TrustedCertificate,
                context.Payload,
                context.Results,
                context.LoopbackCleanupFailures,
                context.CancellationToken)).ConfigureAwait(false);
    }

    private static async Task RunPackagedCaseAsync(
        string reportCaseName,
        Func<Aria2TlsCaseContext, Task> runCaseAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportCaseName);
        ArgumentNullException.ThrowIfNull(runCaseAsync);
        var binaryPath = Environment.GetEnvironmentVariable("DOWNKYI_ARIA2_BINARY");
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            Assert.Skip("DOWNKYI_ARIA2_BINARY is required for the packaged aria2 TLS integration test.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var payload = CreatePayload(4 * 1024 * 1024 + 257);
        using var trustedAuthority = new TestCertificateAuthority(
            $"DownKyi TLS Test {Guid.NewGuid():N}");
        using var trustedCertificate = trustedAuthority.IssueServerCertificate();
        var failures = new Aria2TlsFailureCollector();
        var loopbackCleanupFailures = new ConcurrentQueue<LoopbackTlsCleanupFailure>();
        Aria2TlsTestRuntime? runtime = await Aria2TlsTestRuntime.StartAsync(
            binaryPath,
            trustedAuthority.RootCertificate,
            cancellationToken).ConfigureAwait(false);
        var results = new List<Aria2TlsCaseResult>();
        var context = new Aria2TlsCaseContext(
            runtime,
            trustedAuthority,
            trustedCertificate,
            payload,
            results,
            loopbackCleanupFailures,
            cancellationToken);
        await failures.RunAsync(
            "primary-test",
            async () =>
            {
                await runCaseAsync(context).ConfigureAwait(false);
                var result = Assert.Single(results);
                Assert.Equal(reportCaseName, result.Name);
                Assert.True(result.Passed, result.Name);
            }).ConfigureAwait(false);
        CaptureLoopbackCleanupFailures(loopbackCleanupFailures, failures);
        await failures.RunAsync(
                "report",
                () => WriteReportFragmentAsync(
                    runtime,
                    reportCaseName,
                    results,
                    CancellationToken.None)).ConfigureAwait(false);
        var runtimeDisposal = runtime.DisposeAsync().AsTask();
        runtime = null;
        await failures.RunAsync(
            "runtime-disposal",
            () => runtimeDisposal).ConfigureAwait(false);
        CaptureLoopbackCleanupFailures(loopbackCleanupFailures, failures);

        failures.ThrowIfAny();
    }

    private static void CaptureLoopbackCleanupFailures(
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        Aria2TlsFailureCollector failures)
    {
        while (cleanupFailures.TryDequeue(out var cleanupFailure))
        {
            failures.Capture(
                $"loopback-tls/{cleanupFailure.Operation}",
                cleanupFailure.Exception);
        }
    }

    private static X509Certificate2 CreateRejectedCertificate(
        string caseName,
        TestCertificateAuthority trustedAuthority,
        TestCertificateAuthority unknownAuthority,
        X509Certificate2 intermediateCertificate)
    {
        return caseName switch
        {
            "unknown-ca" => unknownAuthority.IssueServerCertificate(),
            "self-signed" => TestCertificateAuthority.CreateSelfSignedServerCertificate(),
            "expired" => trustedAuthority.IssueServerCertificate(
                notBefore: DateTimeOffset.UtcNow.AddHours(-12),
                notAfter: DateTimeOffset.UtcNow.AddHours(-6)),
            "not-yet-valid" => trustedAuthority.IssueServerCertificate(
                notBefore: DateTimeOffset.UtcNow.AddHours(6),
                notAfter: DateTimeOffset.UtcNow.AddHours(18)),
            "hostname-mismatch" => trustedAuthority.IssueServerCertificate(
                dnsSubjectAlternativeName: "wrong.invalid"),
            "missing-san-wrong-common-name" => trustedAuthority.IssueServerCertificate(
                commonName: "wrong.invalid",
                dnsSubjectAlternativeName: null),
            "incomplete-chain" => trustedAuthority.IssueServerCertificate(
                issuer: intermediateCertificate),
            _ => throw new ArgumentOutOfRangeException(
                nameof(caseName),
                caseName,
                "Unknown rejected-certificate integration case.")
        };
    }

    private static IReadOnlyList<string> GetAcceptedCertificateErrorCodes(string caseName)
    {
        return caseName switch
        {
            "unknown-ca" or "self-signed" =>
            [
                "download.transfer.tls.untrusted",
                "download.transfer.tls.handshake",
                "download.transfer.tls.chain"
            ],
            "expired" =>
            [
                "download.transfer.tls.expired",
                "download.transfer.tls.handshake",
                "download.transfer.tls.chain"
            ],
            "not-yet-valid" =>
            [
                "download.transfer.tls.not-yet-valid",
                "download.transfer.tls.expired",
                "download.transfer.tls.handshake",
                "download.transfer.tls.chain"
            ],
            "hostname-mismatch" or "missing-san-wrong-common-name" =>
            [
                "download.transfer.tls.hostname",
                "download.transfer.tls.handshake",
                "download.transfer.tls.chain"
            ],
            "incomplete-chain" =>
            [
                "download.transfer.tls.chain",
                "download.transfer.tls.untrusted",
                "download.transfer.tls.handshake"
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(caseName),
                caseName,
                "Unknown rejected-certificate integration case.")
        };
    }

    private static async Task RunTrustedSplitDownloadAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            chunkDelay: TimeSpan.FromMilliseconds(10),
            cleanupFailureSink: cleanupFailures);
        await using var serverLifetime = server.ConfigureAwait(false);
        const string outputName = "trusted-split.bin";
        var gid = await runtime.AddDownloadAsync(
            server.Url,
            outputName,
            split: 4,
            maximumTries: 1,
            headers: ["X-DownKyi-Tls-Test: trusted"],
            cancellationToken).ConfigureAwait(false);
        var status = await runtime.WaitForTerminalStatusAsync(
            gid,
            DownloadTimeout,
            cancellationToken).ConfigureAwait(false);
        var outputPath = runtime.GetOutputPath(outputName);
        AssertCompleted(status, "trusted-split", server.Failures);
        AssertPayload(payload, outputPath);
        Assert.Contains(
            server.Requests,
            request => request.RangeStart > 0);
        Assert.Contains(
            server.Requests,
            request => request.Headers.TryGetValue("X-DownKyi-Tls-Test", out var value)
                && string.Equals(value, "trusted", StringComparison.Ordinal));
        results.Add(new Aria2TlsCaseResult("trusted-split", true, "complete"));
    }

    private static async Task RunTrustedConnectProxyDownloadAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            cleanupFailureSink: cleanupFailures);
        await using var serverLifetime = server.ConfigureAwait(false);
        var proxy = new LoopbackHttpConnectProxy();
        var proxyFailures = new Aria2TlsFailureCollector();
        await proxyFailures.RunAsync(
            "primary-test",
            async () =>
            {
                const string outputName = "trusted-connect-proxy.bin";
                var url = new UriBuilder(server.Url)
                {
                    Query = "signature=fixture"
                }.Uri;
                var gid = await runtime.AddDownloadAsync(
                    url,
                    outputName,
                    split: 1,
                    maximumTries: 1,
                    headers: ["Cookie: test-session=fixture"],
                    httpsProxy: proxy.Address.AbsoluteUri,
                    cancellationToken).ConfigureAwait(false);
                var status = await runtime.WaitForTerminalStatusAsync(
                    gid,
                    DownloadTimeout,
                    cancellationToken).ConfigureAwait(false);

                AssertCompleted(status, "trusted-local-connect-proxy", server.Failures);
                AssertPayload(payload, runtime.GetOutputPath(outputName));
                Assert.NotEmpty(proxy.ConnectAuthorities);
                Assert.All(
                    proxy.ConnectAuthorities,
                    authority => Assert.Equal($"localhost:{server.Url.Port}", authority));
                Assert.Equal(0, proxy.AbsoluteUriRequestCount);
                Assert.Equal(0, proxy.CookieHeaderCount);
                Assert.Equal(0, proxy.ProxyAuthorizationHeaderCount);
                Assert.Equal(0, proxy.NonConnectRequestCount);
                Assert.Contains(
                    server.Requests,
                    request => request.Headers.ContainsKey("Cookie"));
                results.Add(new Aria2TlsCaseResult(
                    "trusted-local-connect-proxy",
                    true,
                    "connect-authority-only"));
            }).ConfigureAwait(false);
        var proxyDisposal = proxy.DisposeAsync().AsTask();
        await proxyFailures.RunAsync(
            "connect-proxy-cleanup",
            () => proxyDisposal).ConfigureAwait(false);
        proxyFailures.ThrowIfAny();
    }

    private static async Task RunProxyInterceptionRejectedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 unknownCertificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var proxy = new LoopbackHttpConnectProxy(unknownCertificate);
        var proxyFailures = new Aria2TlsFailureCollector();
        await proxyFailures.RunAsync(
            "primary-test",
            async () =>
            {
                const string outputName = "proxy-untrusted-interception.bin";
                var gid = await runtime.AddDownloadAsync(
                    new Uri("https://localhost:443/media.bin"),
                    outputName,
                    split: 1,
                    maximumTries: 1,
                    headers: ["Cookie: test-session=fixture"],
                    httpsProxy: proxy.Address.AbsoluteUri,
                    cancellationToken).ConfigureAwait(false);
                var status = await runtime.WaitForTerminalStatusAsync(
                    gid,
                    DownloadTimeout,
                    cancellationToken).ConfigureAwait(false);
                var classification = AssertRejectedTlsStatus(
                    status,
                    runtime.GetOutputPath(outputName),
                    payload,
                    [
                        "download.transfer.tls.untrusted",
                        "download.transfer.tls.handshake",
                        "download.transfer.tls.chain"
                    ]);

                Assert.Contains("localhost:443", proxy.ConnectAuthorities);
                Assert.Equal(0, proxy.AbsoluteUriRequestCount);
                Assert.Equal(0, proxy.CookieHeaderCount);
                Assert.Equal(0, proxy.ProxyAuthorizationHeaderCount);
                Assert.Equal(0, proxy.NonConnectRequestCount);
                results.Add(new Aria2TlsCaseResult(
                    "proxy-untrusted-interception",
                    true,
                    classification));
            }).ConfigureAwait(false);
        var proxyDisposal = proxy.DisposeAsync().AsTask();
        await proxyFailures.RunAsync(
            "connect-proxy-cleanup",
            () => proxyDisposal).ConfigureAwait(false);
        proxyFailures.ThrowIfAny();
    }

    private static async Task RunTrustedResumeAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            truncateFirstResponse: true,
            cleanupFailureSink: cleanupFailures);
        await using var serverLifetime = server.ConfigureAwait(false);
        const string outputName = "trusted-resume.bin";
        var gid = await runtime.AddDownloadAsync(
            server.Url,
            outputName,
            split: 1,
            maximumTries: 2,
            headers: null,
            cancellationToken).ConfigureAwait(false);
        var status = await runtime.WaitForTerminalStatusAsync(
            gid,
            DownloadTimeout,
            cancellationToken).ConfigureAwait(false);
        AssertCompleted(status, "trusted-resume", server.Failures);
        AssertPayload(payload, runtime.GetOutputPath(outputName));
        Assert.Contains(server.Requests, request => request.RangeStart > 0);
        results.Add(new Aria2TlsCaseResult("trusted-resume", true, "complete"));
    }

    private static async Task RunRpcRemovalAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            chunkDelay: TimeSpan.FromMilliseconds(100),
            cleanupFailureSink: cleanupFailures);
        await using var serverLifetime = server.ConfigureAwait(false);
        var gid = await runtime.AddDownloadAsync(
            server.Url,
            "rpc-removal.bin",
            split: 1,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);
        await WaitForActiveStatusAsync(runtime, gid, cancellationToken).ConfigureAwait(false);
        var removed = await runtime.Client
            .ForceRemoveAsync(gid, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(gid, removed.Result);
        var status = await runtime.WaitForTerminalStatusAsync(
            gid,
            DownloadTimeout,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("removed", status.Status);
        results.Add(new Aria2TlsCaseResult("rpc-add-query-remove", true, "removed"));
    }

    private static async Task RunRejectedCertificateAsync(
        Aria2TlsTestRuntime runtime,
        string name,
        X509Certificate2 certificate,
        byte[] payload,
        IReadOnlyList<string> acceptedErrorCodes,
        List<Aria2TlsCaseResult> results,
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            cleanupFailureSink: cleanupFailures);
        await using var serverLifetime = server.ConfigureAwait(false);
        var outputName = $"rejected-{name}.bin";
        var gid = await runtime.AddDownloadAsync(
            server.Url,
            outputName,
            split: 1,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);
        var status = await runtime.WaitForTerminalStatusAsync(
            gid,
            DownloadTimeout,
            cancellationToken).ConfigureAwait(false);
        var classification = AssertRejectedTlsStatus(
            status,
            runtime.GetOutputPath(outputName),
            payload,
            acceptedErrorCodes);
        results.Add(new Aria2TlsCaseResult(name, true, classification));
    }

    private static async Task RunRedirectToUntrustedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 trustedCertificate,
        X509Certificate2 unknownCertificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        CancellationToken cancellationToken)
    {
        var target = new LoopbackTlsFileServer(
            _ => unknownCertificate,
            payload,
            cleanupFailureSink: cleanupFailures);
        await using var targetLifetime = target.ConfigureAwait(false);
        var redirect = new LoopbackTlsFileServer(
            _ => trustedCertificate,
            [],
            redirectTarget: target.Url,
            cleanupFailureSink: cleanupFailures);
        await using var redirectLifetime = redirect.ConfigureAwait(false);
        const string outputName = "redirect-to-untrusted.bin";
        var gid = await runtime.AddDownloadAsync(
            redirect.Url,
            outputName,
            split: 1,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);
        var status = await runtime.WaitForTerminalStatusAsync(
            gid,
            DownloadTimeout,
            cancellationToken).ConfigureAwait(false);
        var classification = AssertRejectedTlsStatus(
            status,
            runtime.GetOutputPath(outputName),
            payload,
            [
                "download.transfer.tls.untrusted",
                "download.transfer.tls.handshake",
                "download.transfer.tls.chain"
            ]);
        Assert.NotEmpty(redirect.Requests);
        results.Add(new Aria2TlsCaseResult(
            "trusted-redirect-to-untrusted",
            true,
            classification));
    }

    private static async Task RunResumeToUntrustedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 trustedCertificate,
        X509Certificate2 unknownCertificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            connection => connection == 1 ? trustedCertificate : unknownCertificate,
            payload,
            truncateFirstResponse: true,
            cleanupFailureSink: cleanupFailures);
        await using var serverLifetime = server.ConfigureAwait(false);
        const string outputName = "resume-to-untrusted.bin";
        var gid = await runtime.AddDownloadAsync(
            server.Url,
            outputName,
            split: 1,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);
        var interruptedStatus = await runtime.WaitForTerminalStatusAsync(
            gid,
            DownloadTimeout,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("error", interruptedStatus.Status);
        Assert.Equal(1, server.ConnectionCount);
        await runtime.Client
            .RemoveDownloadResultAsync(gid, cancellationToken)
            .ConfigureAwait(false);

        var retryGid = await runtime.AddDownloadAsync(
            server.Url,
            outputName,
            split: 1,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);
        var status = await runtime.WaitForTerminalStatusAsync(
            retryGid,
            DownloadTimeout,
            cancellationToken).ConfigureAwait(false);
        var classification = AssertRejectedTlsStatus(
            status,
            runtime.GetOutputPath(outputName),
            payload,
            [
                "download.transfer.tls.untrusted",
                "download.transfer.tls.handshake",
                "download.transfer.tls.chain"
            ]);
        Assert.True(server.ConnectionCount >= 2);
        results.Add(new Aria2TlsCaseResult(
            "application-retry-resumes-to-untrusted",
            true,
            classification));
    }

    private static string AssertRejectedTlsStatus(
        AriaTellStatusResult status,
        string outputPath,
        byte[] payload,
        IReadOnlyList<string> acceptedErrorCodes)
    {
        Assert.Equal("error", status.Status);
        Assert.Equal("1", status.ErrorCode);
        Assert.True(
            TlsFailureClassifier.TryClassify(status.ErrorMessage, out var errorCode),
            "aria2 TLS failure was not classified as a safe TLS diagnostic.");
        Assert.Contains(errorCode, acceptedErrorCodes);
        Assert.False(IsCompletePayload(payload, outputPath));
        return errorCode;
    }

    private static void AssertCompleted(
        AriaTellStatusResult status,
        string caseName,
        IReadOnlyList<Exception> serverFailures)
    {
        TlsFailureClassifier.TryClassify(status.ErrorMessage, out var tlsErrorCode);
        var serverFailure = serverFailures.Count > 0 ? serverFailures[0] : null;
        Assert.True(
            string.Equals(status.Status, "complete", StringComparison.Ordinal),
            $"{caseName} failed with aria2 code '{status.ErrorCode}', TLS category '{tlsErrorCode}', and server failure type '{serverFailure?.GetType().Name}'.");
    }

    private static async Task WaitForActiveStatusAsync(
        Aria2TlsTestRuntime runtime,
        string gid,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + DownloadTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await runtime.Client.TellStatus(gid).ConfigureAwait(false);
            if (response.Result is { } status
                && (string.Equals(status.Status, "active", StringComparison.Ordinal)
                    || string.Equals(status.Status, "waiting", StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("aria2 did not expose an active task before removal.");
    }

    private static void AssertPayload(byte[] expected, string path)
    {
        Assert.True(File.Exists(path));
        Assert.Equal(expected.LongLength, new FileInfo(path).Length);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static bool IsCompletePayload(byte[] expected, string path)
    {
        return File.Exists(path)
            && new FileInfo(path).Length == expected.LongLength
            && SHA256.HashData(expected).SequenceEqual(
                SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }

    private static async Task WriteReportFragmentAsync(
        Aria2TlsTestRuntime runtime,
        string reportCaseName,
        IReadOnlyCollection<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var reportDirectory = Environment.GetEnvironmentVariable("DOWNKYI_ARIA2_TLS_REPORT");
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            return;
        }

        if (reportCaseName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException(
                "The aria2 TLS report case name is not safe for use as a file name.");
        }

        var context = new Aria2TlsReportContext(
            Runtime: RuntimeInformation.FrameworkDescription,
            OperatingSystem: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            AssetRuntimeIdentifier: Environment.GetEnvironmentVariable("DOWNKYI_ARIA2_RID")
                ?? "unspecified",
            CommitSha: Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unavailable",
            AriaVersion: runtime.AriaVersion,
            BinarySha256: runtime.BinarySha256,
            RequiredFeature: Aria2TlsTestRuntime.SecureRedirectFeature,
            TlsBackend: GetTlsBackend(),
            CertificateAuthoritySource: runtime.CertificateAuthoritySource);
        var report = Aria2TlsReportWriter.Build(
            ExpectedReportCaseCount,
            results,
            context);
        var reportJson = Aria2TlsReportWriter.EnsureSanitized(
            Aria2TlsReportWriter.Serialize(report));
        var reportPath = Path.Combine(reportDirectory, $"{reportCaseName}.json");
        await Aria2TlsReportWriter.WriteAsync(
            reportPath,
            reportJson,
            cancellationToken).ConfigureAwait(false);
    }

    private static string GetTlsBackend()
    {
        if (OperatingSystem.IsWindows())
        {
            return "WinTLS";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "AppleTLS";
        }

        return "OpenSSL";
    }

    private sealed record Aria2TlsCaseContext(
        Aria2TlsTestRuntime Runtime,
        TestCertificateAuthority TrustedAuthority,
        X509Certificate2 TrustedCertificate,
        byte[] Payload,
        List<Aria2TlsCaseResult> Results,
        ConcurrentQueue<LoopbackTlsCleanupFailure> LoopbackCleanupFailures,
        CancellationToken CancellationToken);
}

[Collection("Aria2 packaged integration")]
public sealed class Aria2RpcLifecycleIntegrationTests
{
    [Fact]
    [Trait("Category", "Aria2TlsIntegration")]
    [Trait("Aria2TlsFamily", "rpc-lifecycle")]
    public async Task PackagedAria2SupportsRpcAddQueryAndRemove()
    {
        await Aria2TlsIntegrationTests.RunRpcLifecycleCaseAsync().ConfigureAwait(true);
    }
}

internal sealed record Aria2TlsCaseResult(
    string Name,
    bool Passed,
    string Outcome);

internal sealed record Aria2TlsReport(
    int SchemaVersion,
    bool Complete,
    bool Passed,
    string Runtime,
    string OperatingSystem,
    string Architecture,
    string RuntimeIdentifier,
    string AssetRuntimeIdentifier,
    string CommitSha,
    string AriaVersion,
    string BinarySha256,
    string RequiredFeature,
    string TlsBackend,
    string CertificateAuthoritySource,
    IReadOnlyCollection<Aria2TlsCaseResult> Cases);
