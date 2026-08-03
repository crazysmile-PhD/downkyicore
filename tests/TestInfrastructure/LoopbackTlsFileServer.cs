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

public sealed class LoopbackTlsFileServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<int, X509Certificate2> _certificateFactory;
    private readonly Func<int, LoopbackTlsRequest, Uri?>? _redirectFactory;
    private readonly byte[] _payload;
    private readonly bool _truncateFirstResponse;
    private readonly TimeSpan _chunkDelay;
    private readonly ConcurrentQueue<LoopbackTlsRequest> _requests = new();
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly TcpListener _listener;
    private readonly Task _serverTask;
    private int _connectionCount;

    public LoopbackTlsFileServer(
        Func<int, X509Certificate2> certificateFactory,
        byte[] payload,
        Uri? redirectTarget = null,
        bool truncateFirstResponse = false,
        TimeSpan chunkDelay = default,
        Func<int, LoopbackTlsRequest, Uri?>? redirectFactory = null)
    {
        _certificateFactory = certificateFactory
            ?? throw new ArgumentNullException(nameof(certificateFactory));
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _redirectFactory = redirectFactory;
        if (_redirectFactory == null && redirectTarget != null)
        {
            _redirectFactory = (_, _) => redirectTarget;
        }
        _truncateFirstResponse = truncateFirstResponse;
        _chunkDelay = chunkDelay;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Url = new Uri($"https://localhost:{endpoint.Port}/media.bin");
        _serverTask = RunAsync(_shutdown.Token);
    }

    public Uri Url { get; }

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public IReadOnlyList<LoopbackTlsRequest> Requests => _requests.ToArray();

    public IReadOnlyList<Exception> Failures => _failures.ToArray();

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
                connections.Add(HandleClientAsync(
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

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        finally
        {
            _listener.Dispose();
            _shutdown.Dispose();
        }
    }
}
