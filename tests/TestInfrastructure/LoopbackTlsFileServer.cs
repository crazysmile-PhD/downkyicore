using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DownKyi.TestInfrastructure;

public sealed record LoopbackTlsRequest(
    string Method,
    string RequestTarget,
    IReadOnlyDictionary<string, string> Headers,
    long? RangeStart,
    long? RangeEnd);

public enum LoopbackTlsCleanupOperation
{
    CancelShutdown,
    StopListener,
    DisposeConnection,
    AwaitServerCompletion,
    DisposeListener,
    DisposeCancellationSource
}

public sealed record LoopbackTlsCleanupFailure(
    LoopbackTlsCleanupOperation Operation,
    int? ConnectionNumber,
    Exception Exception);

public sealed class LoopbackTlsFileServer : IAsyncDisposable
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentQueue<LoopbackTlsCleanupFailure> _cleanupFailures = new();
    private readonly ConcurrentQueue<LoopbackTlsCleanupFailure>? _cleanupFailureSink;
    private readonly object _disposeGate = new();
    private readonly LoopbackTlsCancellation _shutdown;
    private readonly Func<int, X509Certificate2> _certificateFactory;
    private readonly Func<int, LoopbackTlsRequest, Uri?>? _redirectFactory;
    private readonly byte[] _payload;
    private readonly bool _truncateFirstResponse;
    private readonly TimeSpan _chunkDelay;
    private readonly ConcurrentQueue<LoopbackTlsRequest> _requests = new();
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly LoopbackTlsListener _listener;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Task _serverTask;
    private int _connectionCount;
    private Task? _disposeTask;

    public LoopbackTlsFileServer(
        Func<int, X509Certificate2> certificateFactory,
        byte[] payload,
        Uri? redirectTarget = null,
        bool truncateFirstResponse = false,
        TimeSpan chunkDelay = default,
        Func<int, LoopbackTlsRequest, Uri?>? redirectFactory = null,
        TimeSpan shutdownTimeout = default,
        ConcurrentQueue<LoopbackTlsCleanupFailure>? cleanupFailureSink = null)
    {
        _certificateFactory = certificateFactory
            ?? throw new ArgumentNullException(nameof(certificateFactory));
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _shutdownTimeout = shutdownTimeout == default
            ? DefaultShutdownTimeout
            : shutdownTimeout > TimeSpan.Zero
                ? shutdownTimeout
                : throw new ArgumentOutOfRangeException(
                    nameof(shutdownTimeout),
                    shutdownTimeout,
                    "The TLS loopback shutdown timeout must be positive.");
        _redirectFactory = redirectFactory;
        if (_redirectFactory == null && redirectTarget != null)
        {
            _redirectFactory = (_, _) => redirectTarget;
        }
        _truncateFirstResponse = truncateFirstResponse;
        _chunkDelay = chunkDelay;
        _cleanupFailureSink = cleanupFailureSink;
        _shutdown = new LoopbackTlsCancellation(_cleanupFailures, cleanupFailureSink);
        _listener = new LoopbackTlsListener(_cleanupFailures, cleanupFailureSink);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Url = new Uri($"https://localhost:{endpoint.Port}/media.bin");
        _serverTask = RunAsync(_shutdown.Token);
    }

    public Uri Url { get; }

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public Task Completion => _serverTask;

    public IReadOnlyList<LoopbackTlsRequest> Requests => _requests.ToArray();

    public IReadOnlyList<Exception> Failures => _failures.ToArray();

    public IReadOnlyList<LoopbackTlsCleanupFailure> CleanupFailures =>
        _cleanupFailures.ToArray();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                var connectionNumber = Interlocked.Increment(ref _connectionCount);
                _clients[connectionNumber] = client;
                connections.Add(ObserveClientAsync(
                    client,
                    connectionNumber,
                    cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    private async Task ObserveClientAsync(
        TcpClient client,
        int connectionNumber,
        CancellationToken cancellationToken)
    {
        var handler = HandleClientAsync(client, connectionNumber, cancellationToken);
        await handler.ContinueWith(
            completed => RecordHandlerCompletion(completed, _failures),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).ConfigureAwait(false);
        _clients.TryRemove(connectionNumber, out _);
    }

    private async Task HandleClientAsync(
        TcpClient client,
        int connectionNumber,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            var networkStream = client.GetStream();
            await using var networkLifetime = networkStream.ConfigureAwait(false);
            var tlsStream = new SslStream(networkStream, leaveInnerStreamOpen: true);
            await using var tlsLifetime = tlsStream.ConfigureAwait(false);
            try
            {
                await tlsStream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificateFactory(connectionNumber),
                        EnabledSslProtocols = SslProtocols.None
                    },
                    cancellationToken).ConfigureAwait(false);
                var request = await ReadRequestAsync(tlsStream, cancellationToken)
                    .ConfigureAwait(false);
                if (request == null)
                {
                    return;
                }

                _requests.Enqueue(request);
                var redirectTarget = _redirectFactory?.Invoke(
                    connectionNumber,
                    request);
                if (redirectTarget != null)
                {
                    await WriteRedirectAsync(
                        tlsStream,
                        redirectTarget,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WritePayloadAsync(
                    tlsStream,
                    request,
                    connectionNumber,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AuthenticationException error)
            {
                _failures.Enqueue(error);
            }
            catch (IOException error) when (!cancellationToken.IsCancellationRequested)
            {
                _failures.Enqueue(error);
            }
            catch (InvalidOperationException error)
            {
                _failures.Enqueue(error);
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<LoopbackTlsRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 1)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line
               && line.Length > 0)
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        var (rangeStart, rangeEnd) = ParseRange(headers);
        return new LoopbackTlsRequest(
            requestParts[0],
            requestParts.Length > 1 ? requestParts[1] : "/",
            headers,
            rangeStart,
            rangeEnd);
    }

    private static async Task WriteRedirectAsync(
        Stream stream,
        Uri redirectTarget,
        CancellationToken cancellationToken)
    {
        var response = $"HTTP/1.1 302 Found\r\nLocation: {redirectTarget}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(response),
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WritePayloadAsync(
        Stream stream,
        LoopbackTlsRequest request,
        int connectionNumber,
        CancellationToken cancellationToken)
    {
        var start = Math.Clamp(request.RangeStart ?? 0, 0, _payload.LongLength);
        var end = Math.Clamp(
            request.RangeEnd ?? (_payload.LongLength - 1),
            start,
            _payload.LongLength - 1);
        var length = end - start + 1;
        var isPartial = request.RangeStart.HasValue;
        var response = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append(isPartial ? "206 Partial Content" : "200 OK")
            .Append("\r\nAccept-Ranges: bytes\r\nContent-Type: application/octet-stream\r\nContent-Length: ")
            .Append(length)
            .Append("\r\n");
        if (isPartial)
        {
            response.Append("Content-Range: bytes ")
                .Append(start)
                .Append('-')
                .Append(end)
                .Append('/')
                .Append(_payload.LongLength)
                .Append("\r\n");
        }

        response.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(response.ToString()),
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var bytesToWrite = length;
        if (_truncateFirstResponse && connectionNumber == 1 && bytesToWrite > 1)
        {
            bytesToWrite /= 2;
        }

        const int chunkSize = 64 * 1024;
        var offset = start;
        var remaining = bytesToWrite;
        while (remaining > 0)
        {
            var count = (int)Math.Min(chunkSize, remaining);
            await stream.WriteAsync(
                _payload.AsMemory((int)offset, count),
                cancellationToken).ConfigureAwait(false);
            offset += count;
            remaining -= count;
            if (_chunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(_chunkDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (long? Start, long? End) ParseRange(
        Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Range", out var range)
            || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var value = range["bytes=".Length..];
        var separator = value.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0 || !long.TryParse(
                value[..separator],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var start))
        {
            return (null, null);
        }

        var endText = value[(separator + 1)..];
        return long.TryParse(
            endText,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var end)
            ? (start, end)
            : (start, null);
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            disposeTask = _disposeTask ??= DisposeCoreAsync();
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        using var cleanupDeadline = new CancellationTokenSource(_shutdownTimeout);
        await RunBoundedCleanupAsync(
            LoopbackTlsCleanupOperation.CancelShutdown,
            connectionNumber: null,
            () => _shutdown.CancelAsync(),
            cleanupDeadline.Token).ConfigureAwait(false);
        await RunCleanupAsync(
            LoopbackTlsCleanupOperation.StopListener,
            connectionNumber: null,
            _listener.Stop,
            cleanupDeadline.Token).ConfigureAwait(false);

        foreach (var connection in _clients.ToArray())
        {
            await RunCleanupAsync(
                LoopbackTlsCleanupOperation.DisposeConnection,
                connection.Key,
                connection.Value.Dispose,
                cleanupDeadline.Token).ConfigureAwait(false);
        }

        await RunBoundedCleanupAsync(
            LoopbackTlsCleanupOperation.AwaitServerCompletion,
            connectionNumber: null,
            () => _serverTask,
            cleanupDeadline.Token).ConfigureAwait(false);
        _listener.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunBoundedCleanupAsync(
        LoopbackTlsCleanupOperation operation,
        int? connectionNumber,
        Func<Task> cleanup,
        CancellationToken cleanupDeadline)
    {
        var cleanupTask = Task.Factory.StartNew(
            cleanup,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
        var completed = await Task.WhenAny(
            cleanupTask,
            Task.Delay(Timeout.InfiniteTimeSpan, cleanupDeadline)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, cleanupTask))
        {
            RecordCleanupFailure(new LoopbackTlsCleanupFailure(
                operation,
                connectionNumber,
                new TimeoutException(
                    $"Loopback TLS cleanup stage '{operation}' exceeded its deadline.")));
            _ = cleanupTask.ContinueWith(
                lateCompletion => RecordCleanupCompletion(
                    lateCompletion,
                    operation,
                    connectionNumber),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        RecordCleanupCompletion(cleanupTask, operation, connectionNumber);
    }

    private Task RunCleanupAsync(
        LoopbackTlsCleanupOperation operation,
        int? connectionNumber,
        Action cleanup,
        CancellationToken cleanupDeadline)
    {
        return RunBoundedCleanupAsync(
            operation,
            connectionNumber,
            () =>
            {
                cleanup();
                return Task.CompletedTask;
            },
            cleanupDeadline);
    }

    private static void RecordHandlerCompletion(
        Task handler,
        ConcurrentQueue<Exception> failures)
    {
        if (handler.IsFaulted)
        {
            foreach (var error in handler.Exception.Flatten().InnerExceptions)
            {
                failures.Enqueue(error);
            }
        }
        else if (handler.IsCanceled)
        {
            failures.Enqueue(new TaskCanceledException(handler));
        }
    }

    private void RecordCleanupCompletion(
        Task cleanup,
        LoopbackTlsCleanupOperation operation,
        int? connectionNumber)
    {
        if (cleanup.IsFaulted)
        {
            foreach (var error in cleanup.Exception.Flatten().InnerExceptions)
            {
                RecordCleanupFailure(new LoopbackTlsCleanupFailure(
                    operation,
                    connectionNumber,
                    error));
            }
        }
        else if (cleanup.IsCanceled)
        {
            RecordCleanupFailure(new LoopbackTlsCleanupFailure(
                operation,
                connectionNumber,
                new TaskCanceledException(cleanup)));
        }
    }

    private void RecordCleanupFailure(LoopbackTlsCleanupFailure failure)
    {
        _cleanupFailures.Enqueue(failure);
        if (_cleanupFailureSink != null
            && !ReferenceEquals(_cleanupFailureSink, _cleanupFailures))
        {
            _cleanupFailureSink.Enqueue(failure);
        }
    }

    private sealed class LoopbackTlsCancellation(
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        ConcurrentQueue<LoopbackTlsCleanupFailure>? cleanupFailureSink) : IDisposable
    {
        private readonly CancellationTokenSource _source = new();

        public CancellationToken Token => _source.Token;

        public Task CancelAsync()
        {
            return _source.CancelAsync();
        }

        public void Dispose()
        {
            try
            {
                _source.Dispose();
            }
            catch (ObjectDisposedException error)
            {
                RecordCleanupFailure(
                    cleanupFailures,
                    cleanupFailureSink,
                    new LoopbackTlsCleanupFailure(
                    LoopbackTlsCleanupOperation.DisposeCancellationSource,
                    ConnectionNumber: null,
                    error));
            }
        }
    }

    private sealed class LoopbackTlsListener(
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        ConcurrentQueue<LoopbackTlsCleanupFailure>? cleanupFailureSink) : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public EndPoint LocalEndpoint => _listener.LocalEndpoint;

        public ValueTask<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
        {
            return _listener.AcceptTcpClientAsync(cancellationToken);
        }

        public void Dispose()
        {
            try
            {
                _listener.Dispose();
            }
            catch (SocketException error)
            {
                RecordCleanupFailure(
                    cleanupFailures,
                    cleanupFailureSink,
                    new LoopbackTlsCleanupFailure(
                    LoopbackTlsCleanupOperation.DisposeListener,
                    ConnectionNumber: null,
                    error));
            }
            catch (ObjectDisposedException error)
            {
                RecordCleanupFailure(
                    cleanupFailures,
                    cleanupFailureSink,
                    new LoopbackTlsCleanupFailure(
                    LoopbackTlsCleanupOperation.DisposeListener,
                    ConnectionNumber: null,
                    error));
            }
        }

        public void Start()
        {
            _listener.Start();
        }

        public void Stop()
        {
            _listener.Stop();
        }
    }

    private static void RecordCleanupFailure(
        ConcurrentQueue<LoopbackTlsCleanupFailure> cleanupFailures,
        ConcurrentQueue<LoopbackTlsCleanupFailure>? cleanupFailureSink,
        LoopbackTlsCleanupFailure failure)
    {
        cleanupFailures.Enqueue(failure);
        if (cleanupFailureSink != null
            && !ReferenceEquals(cleanupFailureSink, cleanupFailures))
        {
            cleanupFailureSink.Enqueue(failure);
        }
    }
}
