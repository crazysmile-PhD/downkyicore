using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Services.Download;
using DownKyi.TestInfrastructure;

namespace DownKyi.Tests;

public sealed partial class Aria2TlsIntegrationTests
{
    private const int ExpectedCaseCount = 26;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true
    };

    [Fact]
    [Trait("Category", "Aria2TlsIntegration")]
    public async Task PackagedAria2EnforcesCertificateValidationAcrossTransferFlows()
    {
        var binaryPath = Environment.GetEnvironmentVariable("DOWNKYI_ARIA2_BINARY");
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            Assert.Skip("DOWNKYI_ARIA2_BINARY is required for the packaged aria2 TLS integration test.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var payload = CreatePayload(4 * 1024 * 1024 + 257);
        using var trustedAuthority = new TestCertificateAuthority(
            $"DownKyi TLS Test {Guid.NewGuid():N}");
        using var unknownAuthority = new TestCertificateAuthority(
            $"DownKyi Unknown TLS Test {Guid.NewGuid():N}");
        using var trustedCertificate = trustedAuthority.IssueServerCertificate();
        using var unknownCertificate = unknownAuthority.IssueServerCertificate();
        using var selfSignedCertificate =
            TestCertificateAuthority.CreateSelfSignedServerCertificate();
        using var expiredCertificate = trustedAuthority.IssueServerCertificate(
            notBefore: DateTimeOffset.UtcNow.AddHours(-12),
            notAfter: DateTimeOffset.UtcNow.AddHours(-6));
        using var notYetValidCertificate = trustedAuthority.IssueServerCertificate(
            notBefore: DateTimeOffset.UtcNow.AddHours(6),
            notAfter: DateTimeOffset.UtcNow.AddHours(18));
        using var hostnameMismatchCertificate = trustedAuthority.IssueServerCertificate(
            dnsSubjectAlternativeName: "wrong.invalid");
        using var missingSanCertificate = trustedAuthority.IssueServerCertificate(
            commonName: "wrong.invalid",
            dnsSubjectAlternativeName: null);
        using var intermediateCertificate =
            trustedAuthority.IssueIntermediateCertificate("DownKyi Test Intermediate");
        using var incompleteChainCertificate = trustedAuthority.IssueServerCertificate(
            issuer: intermediateCertificate);

        var runtime = await Aria2TlsTestRuntime.StartAsync(
            binaryPath,
            trustedAuthority.RootCertificate,
            cancellationToken).ConfigureAwait(true);
        await using var runtimeLifetime = runtime.ConfigureAwait(false);
        var results = new List<Aria2TlsCaseResult>();
        try
        {
            await RunTrustedSplitDownloadAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunHttpsRedirectToHttpRejectedAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunPreflightThenActualDowngradeRejectedAsync(
                runtime,
                trustedCertificate,
                trustedAuthority.RootCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunHeadSafeGetDowngradeRejectedAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunRangeDowngradeRejectedAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunSecondRoundDowngradeRejectedAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunSensitiveCrossOriginRedirectsRejectedAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunSameOriginHttpsRedirectAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunCrossOriginHttpsRedirectAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunTrustedConnectProxyDownloadAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunProxyInterceptionRejectedAsync(
                runtime,
                unknownCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunTrustedResumeAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunRpcRemovalAsync(
                runtime,
                trustedCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);

            await RunRejectedCertificateAsync(
                runtime,
                "unknown-ca",
                unknownCertificate,
                payload,
                [
                    "download.transfer.tls.untrusted",
                    "download.transfer.tls.handshake",
                    "download.transfer.tls.chain"
                ],
                results,
                cancellationToken).ConfigureAwait(true);
            await RunRejectedCertificateAsync(
                runtime,
                "self-signed",
                selfSignedCertificate,
                payload,
                [
                    "download.transfer.tls.untrusted",
                    "download.transfer.tls.handshake",
                    "download.transfer.tls.chain"
                ],
                results,
                cancellationToken).ConfigureAwait(true);
            await RunRejectedCertificateAsync(
                runtime,
                "expired",
                expiredCertificate,
                payload,
                [
                    "download.transfer.tls.expired",
                    "download.transfer.tls.handshake",
                    "download.transfer.tls.chain"
                ],
                results,
                cancellationToken).ConfigureAwait(true);
            await RunRejectedCertificateAsync(
                runtime,
                "not-yet-valid",
                notYetValidCertificate,
                payload,
                [
                    "download.transfer.tls.not-yet-valid",
                    "download.transfer.tls.expired",
                    "download.transfer.tls.handshake",
                    "download.transfer.tls.chain"
                ],
                results,
                cancellationToken).ConfigureAwait(true);
            await RunRejectedCertificateAsync(
                runtime,
                "hostname-mismatch",
                hostnameMismatchCertificate,
                payload,
                [
                    "download.transfer.tls.hostname",
                    "download.transfer.tls.handshake",
                    "download.transfer.tls.chain"
                ],
                results,
                cancellationToken).ConfigureAwait(true);
            await RunRejectedCertificateAsync(
                runtime,
                "missing-san-wrong-common-name",
                missingSanCertificate,
                payload,
                [
                    "download.transfer.tls.hostname",
                    "download.transfer.tls.handshake",
                    "download.transfer.tls.chain"
                ],
                results,
                cancellationToken).ConfigureAwait(true);
            await RunRejectedCertificateAsync(
                runtime,
                "incomplete-chain",
                incompleteChainCertificate,
                payload,
                [
                    "download.transfer.tls.chain",
                    "download.transfer.tls.untrusted",
                    "download.transfer.tls.handshake"
                ],
                results,
                cancellationToken).ConfigureAwait(true);
            await RunRedirectToUntrustedAsync(
                runtime,
                trustedCertificate,
                unknownCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);
            await RunResumeToUntrustedAsync(
                runtime,
                trustedCertificate,
                unknownCertificate,
                payload,
                results,
                cancellationToken).ConfigureAwait(true);

            Assert.All(results, result => Assert.True(result.Passed, result.Name));
        }
        finally
        {
            await WriteReportAsync(
                runtime,
                results,
                CancellationToken.None).ConfigureAwait(true);
        }
    }

    private static async Task RunTrustedSplitDownloadAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            chunkDelay: TimeSpan.FromMilliseconds(10));
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
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(_ => certificate, payload);
        await using var serverLifetime = server.ConfigureAwait(false);
        var proxy = new LoopbackHttpConnectProxy();
        await using var proxyLifetime = proxy.ConfigureAwait(false);
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
    }

    private static async Task RunProxyInterceptionRejectedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 unknownCertificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var proxy = new LoopbackHttpConnectProxy(unknownCertificate);
        await using var proxyLifetime = proxy.ConfigureAwait(false);
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
    }

    private static async Task RunTrustedResumeAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            truncateFirstResponse: true);
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
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            chunkDelay: TimeSpan.FromMilliseconds(100));
        await using var serverLifetime = server.ConfigureAwait(false);
        var gid = await runtime.AddDownloadAsync(
            server.Url,
            "rpc-removal.bin",
            split: 1,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);
        await WaitForActiveStatusAsync(runtime, gid, cancellationToken).ConfigureAwait(false);
        var removed = await runtime.Client.ForceRemoveAsync(gid).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(_ => certificate, payload);
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
        CancellationToken cancellationToken)
    {
        var target = new LoopbackTlsFileServer(_ => unknownCertificate, payload);
        await using var targetLifetime = target.ConfigureAwait(false);
        var redirect = new LoopbackTlsFileServer(
            _ => trustedCertificate,
            [],
            redirectTarget: target.Url);
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
        CancellationToken cancellationToken)
    {
        var server = new LoopbackTlsFileServer(
            connection => connection == 1 ? trustedCertificate : unknownCertificate,
            payload,
            truncateFirstResponse: true);
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
        await runtime.Client.RemoveDownloadResultAsync(gid).ConfigureAwait(false);

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

    private static async Task WriteReportAsync(
        Aria2TlsTestRuntime runtime,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var reportPath = Environment.GetEnvironmentVariable("DOWNKYI_ARIA2_TLS_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        var report = new Aria2TlsReport(
            SchemaVersion: 2,
            Complete: results.Count == ExpectedCaseCount,
            Passed: results.Count == ExpectedCaseCount && results.All(result => result.Passed),
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
            CertificateAuthoritySource: runtime.CertificateAuthoritySource,
            Cases: results);
        var reportJson = JsonSerializer.Serialize(report, ReportJsonOptions);
        AssertSanitizedReport(reportJson);
        await File.WriteAllTextAsync(
            reportPath,
            reportJson,
            cancellationToken).ConfigureAwait(false);
    }

    private static void AssertSanitizedReport(string reportJson)
    {
        string[] forbiddenTerms =
        {
            "test-session=fixture",
            "Bearer fixture",
            "Basic Zml4dHVyZQ==",
            "X-Access-Token: fixture",
            "X-API-Key: fixture",
            "sessdata",
            "bili_jct",
            "dedeuserid",
            "http://",
            "https://",
            "C:\\Users\\",
            "/Users/",
            "/home/"
        };
        foreach (var forbiddenTerm in forbiddenTerms)
        {
            Assert.DoesNotContain(
                forbiddenTerm,
                reportJson,
                StringComparison.OrdinalIgnoreCase);
        }
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
