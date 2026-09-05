using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DownKyi.TestInfrastructure;

namespace DownKyi.Tests;

public sealed class LoopbackTlsFileServerLifecycleTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DisposeClosesActiveConnectionWithinTheShutdownBound()
    {
        using var certificate = TestCertificateAuthority.CreateSelfSignedServerCertificate();
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            "response"u8.ToArray(),
            shutdownTimeout: TimeSpan.FromSeconds(1));
        await using var serverLifetime = server.ConfigureAwait(false);
        using var client = new TcpClient();
        await client.ConnectAsync(
            IPAddress.Loopback,
            server.Url.Port,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        await WaitUntilAsync(
            () => server.ConnectionCount == 1,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        var elapsed = Stopwatch.StartNew();
        await server.DisposeAsync().ConfigureAwait(true);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < ObservationTimeout, $"Cleanup took {elapsed.Elapsed}.");
        Assert.True(server.Completion.IsCompleted);
        Assert.Empty(server.CleanupFailures);
    }

    [Fact]
    public async Task HandlerFailureIsObservableWithoutReplacingThePrimaryFailure()
    {
        var handlerFailure = new InvalidDataException("Synthetic TLS handler failure.");
        var primaryFailure = new InvalidOperationException("Primary business failure.");
        var server = new LoopbackTlsFileServer(
            _ => throw handlerFailure,
            "response"u8.ToArray(),
            shutdownTimeout: TimeSpan.FromSeconds(1));
        await using var serverOwnership = server.ConfigureAwait(false);

        async Task ThrowPrimaryFailureAsync()
        {
            await using var serverLifetime = server.ConfigureAwait(false);
            using var client = new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                server.Url.Port,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            await WaitUntilAsync(
                () => server.Failures.Any(
                    failure => ReferenceEquals(failure, handlerFailure)),
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            throw primaryFailure;
        }

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            ThrowPrimaryFailureAsync).ConfigureAwait(true);

        Assert.Same(primaryFailure, actual);
        Assert.Contains(server.Failures, failure => ReferenceEquals(failure, handlerFailure));
        Assert.Empty(server.CleanupFailures);
    }

    [Fact]
    public async Task DisposeRecordsConnectionCompletionTimeoutAndReleasesResources()
    {
        using var certificate = TestCertificateAuthority.CreateSelfSignedServerCertificate();
        using var handlerEntered = new ManualResetEventSlim();
        using var releaseHandler = new ManualResetEventSlim();
        var cleanupFailureSink = new ConcurrentQueue<LoopbackTlsCleanupFailure>();
        var server = new LoopbackTlsFileServer(
            _ =>
            {
                handlerEntered.Set();
                releaseHandler.Wait();
                return certificate;
            },
            "response"u8.ToArray(),
            shutdownTimeout: TimeSpan.FromMilliseconds(100),
            cleanupFailureSink: cleanupFailureSink);
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback,
                server.Url.Port,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await WaitUntilAsync(
                () => handlerEntered.IsSet,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            var elapsed = Stopwatch.StartNew();
            await server.DisposeAsync().ConfigureAwait(true);
            elapsed.Stop();

            Assert.True(elapsed.Elapsed < ObservationTimeout, $"Cleanup took {elapsed.Elapsed}.");
            var timeout = Assert.Single(
                server.CleanupFailures,
                failure => failure.Operation
                    == LoopbackTlsCleanupOperation.AwaitServerCompletion);
            Assert.IsType<TimeoutException>(timeout.Exception);
            var reportedTimeout = Assert.Single(
                cleanupFailureSink,
                failure => failure.Operation
                    == LoopbackTlsCleanupOperation.AwaitServerCompletion);
            Assert.Same(timeout.Exception, reportedTimeout.Exception);

            using var reconnect = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(async () =>
                await reconnect.ConnectAsync(
                    IPAddress.Loopback,
                    server.Url.Port,
                    TestContext.Current.CancellationToken).ConfigureAwait(false))
                .ConfigureAwait(true);
        }
        finally
        {
            releaseHandler.Set();
            await server.DisposeAsync().ConfigureAwait(true);
            await server.Completion.WaitAsync(
                ObservationTimeout,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ObservationTimeout;
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected loopback TLS state was not observed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
