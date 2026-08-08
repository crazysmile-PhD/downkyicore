using System.Net;
using System.Security.Cryptography.X509Certificates;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Services.Download;
using DownKyi.TestInfrastructure;

namespace DownKyi.Tests;

public sealed partial class Aria2TlsIntegrationTests
{
    private static async Task RunHttpsRedirectToHttpRejectedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var target = CreatePlainHttpTarget();
        await using var targetLifetime = target.ConfigureAwait(false);
        var redirect = new LoopbackTlsFileServer(
            _ => certificate,
            [],
            redirectTarget: target.Url);
        await using var redirectLifetime = redirect.ConfigureAwait(false);

        const string outputName = "https-to-http-redirect.bin";
        var status = await DownloadToTerminalStatusAsync(
            runtime,
            redirect.Url,
            outputName,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);

        AssertRedirectRejected(
            status,
            runtime.GetOutputPath(outputName),
            payload,
            "download.transfer.insecure-redirect");
        Assert.Equal(0, target.RequestCount);
        results.Add(new Aria2TlsCaseResult(
            "https-to-http-redirect",
            true,
            "zero-http-requests"));
    }

    private static async Task RunPreflightThenActualDowngradeRejectedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        X509Certificate2 trustedRoot,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var target = CreatePlainHttpTarget();
        await using var targetLifetime = target.ConfigureAwait(false);
        var redirect = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            redirectFactory: (connection, _) => connection > 1 ? target.Url : null);
        await using var redirectLifetime = redirect.ConfigureAwait(false);
        using var resolver = CreateTrustedAddressResolver(trustedRoot);
        var resolution = await resolver.ResolveAsync(
            redirect.Url.AbsoluteUri,
            "DownKyi-TLS-Test",
            credentials: null,
            cancellationToken).ConfigureAwait(false);
        var acceptedAddress = Assert.IsType<Uri>(resolution.Address);
        var acceptedHeaders = Assert.IsType<AriaTaskHeaders>(resolution.Headers);
        Assert.Null(resolution.ErrorCode);

        const string outputName = "preflight-then-actual-downgrade.bin";
        var status = await DownloadToTerminalStatusAsync(
            runtime,
            acceptedAddress,
            outputName,
            maximumTries: 1,
            acceptedHeaders.Headers,
            cancellationToken).ConfigureAwait(false);

        AssertRedirectRejected(
            status,
            runtime.GetOutputPath(outputName),
            payload,
            "download.transfer.insecure-redirect");
        Assert.Equal(0, target.RequestCount);
        Assert.True(redirect.ConnectionCount >= 2);
        results.Add(new Aria2TlsCaseResult(
            "preflight-safe-actual-downgrade",
            true,
            "zero-http-requests"));
    }

    private static AriaDownloadAddressResolver CreateTrustedAddressResolver(
        X509Certificate2 trustedRoot)
    {
        var chainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        chainPolicy.CustomTrustStore.Add(trustedRoot);

        SocketsHttpHandler? handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            UseProxy = false
        };
        handler.SslOptions.CertificateChainPolicy = chainPolicy;
        try
        {
            var resolver = AriaDownloadAddressResolver.CreateForTest(handler);
            handler = null;
            return resolver;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    private static async Task RunHeadSafeGetDowngradeRejectedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var target = CreatePlainHttpTarget();
        await using var targetLifetime = target.ConfigureAwait(false);
        var redirect = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            redirectFactory: (_, request) =>
                string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                    ? target.Url
                    : null);
        await using var redirectLifetime = redirect.ConfigureAwait(false);

        using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
        {
            var expectedCertificateHash = certificate.GetCertHashString(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            handler.ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                presented != null
                && string.Equals(
                    presented.GetCertHashString(
                        System.Security.Cryptography.HashAlgorithmName.SHA256),
                    expectedCertificateHash,
                    StringComparison.Ordinal);
            using var client = new HttpClient(handler, disposeHandler: false);
            using var request = new HttpRequestMessage(HttpMethod.Head, redirect.Url);
            using var response = await client.SendAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        const string outputName = "head-safe-get-downgrade.bin";
        var status = await DownloadToTerminalStatusAsync(
            runtime,
            redirect.Url,
            outputName,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);

        AssertRedirectRejected(
            status,
            runtime.GetOutputPath(outputName),
            payload,
            "download.transfer.insecure-redirect");
        Assert.Equal(0, target.RequestCount);
        Assert.Contains(
            redirect.Requests,
            request => string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            redirect.Requests,
            request => string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase));
        results.Add(new Aria2TlsCaseResult(
            "head-safe-get-downgrade",
            true,
            "zero-http-requests"));
    }

    private static async Task RunRangeDowngradeRejectedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var target = CreatePlainHttpTarget();
        await using var targetLifetime = target.ConfigureAwait(false);
        var redirect = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            redirectFactory: (_, request) => request.RangeStart is > 0 ? target.Url : null);
        await using var redirectLifetime = redirect.ConfigureAwait(false);

        const string outputName = "range-downgrade.bin";
        var outputPath = runtime.GetOutputPath(outputName);
        await File.WriteAllBytesAsync(
            outputPath,
            payload[..(payload.Length / 2)],
            cancellationToken).ConfigureAwait(false);
        var status = await DownloadToTerminalStatusAsync(
            runtime,
            redirect.Url,
            outputName,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);

        AssertRedirectRejected(
            status,
            outputPath,
            payload,
            "download.transfer.insecure-redirect");
        Assert.Equal(0, target.RequestCount);
        Assert.Contains(redirect.Requests, request => request.RangeStart is > 0);
        results.Add(new Aria2TlsCaseResult(
            "range-downgrade",
            true,
            "zero-http-requests"));
    }

    private static async Task RunSecondRoundDowngradeRejectedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var target = CreatePlainHttpTarget();
        await using var targetLifetime = target.ConfigureAwait(false);
        var redirect = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            truncateFirstResponse: true,
            redirectFactory: (connection, _) => connection > 1 ? target.Url : null);
        await using var redirectLifetime = redirect.ConfigureAwait(false);
        const string outputName = "second-round-downgrade.bin";
        var firstGid = await runtime.AddDownloadAsync(
            redirect.Url,
            outputName,
            split: 1,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);
        var firstStatus = await runtime.WaitForTerminalStatusAsync(
            firstGid,
            DownloadTimeout,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("error", firstStatus.Status);
        Assert.Equal(1, redirect.ConnectionCount);
        await runtime.Client
            .RemoveDownloadResultAsync(firstGid, cancellationToken)
            .ConfigureAwait(false);

        var secondStatus = await DownloadToTerminalStatusAsync(
            runtime,
            redirect.Url,
            outputName,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);
        AssertRedirectRejected(
            secondStatus,
            runtime.GetOutputPath(outputName),
            payload,
            "download.transfer.insecure-redirect");
        Assert.Equal(0, target.RequestCount);
        Assert.True(redirect.ConnectionCount >= 2);
        results.Add(new Aria2TlsCaseResult(
            "second-round-downgrade",
            true,
            "zero-http-requests"));
    }

    private static async Task RunSensitiveCrossOriginRedirectsRejectedAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var cases = new (string ReportName, string Header)[]
        {
            ("sensitive-cross-origin-cookie", "Cookie: test-session=fixture"),
            ("sensitive-cross-origin-authorization", "Authorization: Bearer fixture"),
            ("sensitive-cross-origin-proxy-authorization", "Proxy-Authorization: Basic Zml4dHVyZQ=="),
            ("sensitive-cross-origin-token", "X-Access-Token: fixture"),
            ("sensitive-cross-origin-api-key", "X-API-Key: fixture")
        };

        foreach (var testCase in cases)
        {
            var target = new LoopbackTlsFileServer(_ => certificate, payload);
            await using var targetLifetime = target.ConfigureAwait(false);
            var redirect = new LoopbackTlsFileServer(
                _ => certificate,
                [],
                redirectTarget: target.Url);
            await using var redirectLifetime = redirect.ConfigureAwait(false);
            var outputName = $"{testCase.ReportName}.bin";
            var status = await DownloadToTerminalStatusAsync(
                runtime,
                redirect.Url,
                outputName,
                maximumTries: 1,
                headers: [testCase.Header],
                cancellationToken).ConfigureAwait(false);

            AssertRedirectRejected(
                status,
                runtime.GetOutputPath(outputName),
                payload,
                "download.transfer.credentialed-redirect");
            Assert.Equal(0, target.ConnectionCount);
            Assert.Empty(target.Requests);
            results.Add(new Aria2TlsCaseResult(
                testCase.ReportName,
                true,
                "zero-cross-origin-requests"));
        }
    }

    private static async Task RunSameOriginHttpsRedirectAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        Uri? finalAddress = null;
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            payload,
            redirectFactory: (_, request) =>
                request.RequestTarget.StartsWith("/media.bin", StringComparison.Ordinal)
                    ? finalAddress
                    : null);
        await using var serverLifetime = server.ConfigureAwait(false);
        finalAddress = new Uri(server.Url, "/final.bin");

        const string outputName = "same-origin-https-redirect.bin";
        var status = await DownloadToTerminalStatusAsync(
            runtime,
            server.Url,
            outputName,
            maximumTries: 1,
            headers: ["Cookie: test-session=fixture"],
            cancellationToken).ConfigureAwait(false);

        AssertCompleted(status, "same-origin-https-redirect", server.Failures);
        AssertPayload(payload, runtime.GetOutputPath(outputName));
        Assert.Contains(
            server.Requests,
            request => request.RequestTarget.StartsWith("/media.bin", StringComparison.Ordinal));
        Assert.Contains(
            server.Requests,
            request => request.RequestTarget.StartsWith("/final.bin", StringComparison.Ordinal));
        Assert.All(
            server.Requests,
            request => Assert.Equal(
                "test-session=fixture",
                request.Headers["Cookie"]));
        results.Add(new Aria2TlsCaseResult(
            "same-origin-https-redirect",
            true,
            "complete"));
    }

    private static async Task RunCrossOriginHttpsRedirectAsync(
        Aria2TlsTestRuntime runtime,
        X509Certificate2 certificate,
        byte[] payload,
        List<Aria2TlsCaseResult> results,
        CancellationToken cancellationToken)
    {
        var target = new LoopbackTlsFileServer(_ => certificate, payload);
        await using var targetLifetime = target.ConfigureAwait(false);
        var redirect = new LoopbackTlsFileServer(
            _ => certificate,
            [],
            redirectTarget: target.Url);
        await using var redirectLifetime = redirect.ConfigureAwait(false);

        const string outputName = "cross-origin-https-redirect.bin";
        var status = await DownloadToTerminalStatusAsync(
            runtime,
            redirect.Url,
            outputName,
            maximumTries: 1,
            headers: null,
            cancellationToken).ConfigureAwait(false);

        AssertCompleted(status, "cross-origin-https-redirect", target.Failures);
        AssertPayload(payload, runtime.GetOutputPath(outputName));
        Assert.NotEmpty(redirect.Requests);
        Assert.NotEmpty(target.Requests);
        results.Add(new Aria2TlsCaseResult(
            "cross-origin-https-redirect",
            true,
            "complete"));
    }

    private static LoopbackHttpServer CreatePlainHttpTarget()
    {
        return new LoopbackHttpServer(_ => new LoopbackResponse(
            HttpStatusCode.OK,
            Body: "blocked-target"));
    }

    private static async Task<AriaTellStatusResult> DownloadToTerminalStatusAsync(
        Aria2TlsTestRuntime runtime,
        Uri address,
        string outputName,
        int maximumTries,
        IReadOnlyList<string>? headers,
        CancellationToken cancellationToken)
    {
        var gid = await runtime.AddDownloadAsync(
            address,
            outputName,
            split: 1,
            maximumTries,
            headers,
            cancellationToken).ConfigureAwait(false);
        return await runtime.WaitForTerminalStatusAsync(
            gid,
            DownloadTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static void AssertRedirectRejected(
        AriaTellStatusResult status,
        string outputPath,
        byte[] payload,
        string expectedErrorCode)
    {
        Assert.Equal("error", status.Status);
        var expectedAriaErrorCode = expectedErrorCode switch
        {
            "download.transfer.insecure-redirect" => "33",
            "download.transfer.credentialed-redirect" => "34",
            _ => throw new ArgumentOutOfRangeException(
                nameof(expectedErrorCode),
                expectedErrorCode,
                "Unknown secure redirect diagnostic.")
        };
        Assert.Equal(expectedAriaErrorCode, status.ErrorCode);
        var classification = Aria2TransferFailureClassifier.Classify(
            status.ErrorCode,
            status.ErrorMessage);
        Assert.Equal(DownloadTransferOutcome.Failed, classification.Outcome);
        Assert.Equal(DownloadTransferFailureKind.Permanent, classification.FailureKind);
        Assert.Equal(expectedErrorCode, classification.ErrorCode);
        Assert.False(IsCompletePayload(payload, outputPath));
    }
}
