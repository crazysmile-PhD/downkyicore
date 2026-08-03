using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DownKyi.TestInfrastructure;

public sealed class LoopbackHttpConnectProxy : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentQueue<string> _connectAuthorities = new();
    private readonly X509Certificate2? _interceptCertificate;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TcpListener _listener;
    private readonly Task _serverTask;
    private int _absoluteUriRequestCount;
    private int _connectionSequence;
    private int _cookieHeaderCount;
    private int _nonConnectRequestCount;
    private int _proxyAuthorizationHeaderCount;

    public LoopbackHttpConnectProxy(X509Certificate2? interceptCertificate = null)
    {
        _interceptCertificate = interceptCertificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Address = new Uri($"http://127.0.0.1:{endpoint.Port}/");
        _serverTask = RunAsync(_shutdown.Token);
    }

    public Uri Address { get; }

    public IReadOnlyList<string> ConnectAuthorities => _connectAuthorities.ToArray();

    public int AbsoluteUriRequestCount => Volatile.Read(ref _absoluteUriRequestCount);

    public int CookieHeaderCount => Volatile.Read(ref _cookieHeaderCount);

    public int NonConnectRequestCount => Volatile.Read(ref _nonConnectRequestCount);

    public int ProxyAuthorizationHeaderCount =>
        Volatile.Read(ref _proxyAuthorizationHeaderCount);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var handlers = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                var id = Interlocked.Increment(ref _connectionSequence);
                _clients[id] = client;
                handlers.Add(HandleClientAsync(id, client, cancellationToken));
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
            await Task.WhenAll(handlers).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(
        int id,
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var clientStream = client.GetStream();
                await using var clientStreamLifetime = clientStream.ConfigureAwait(false);
                var request = await ReadConnectRequestAsync(
                    clientStream,
                    cancellationToken).ConfigureAwait(false);
                if (request == null)
                {
                    return;
                }

                RecordRequestMetadata(request);
                if (!string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _nonConnectRequestCount);
                    await WriteResponseAsync(
                        clientStream,
                        "HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\n\r\n",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                _connectAuthorities.Enqueue(request.Target);
                if (_interceptCertificate != null)
                {
                    await InterceptAsync(
                        clientStream,
                        _interceptCertificate,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                await TunnelAsync(
                    clientStream,
                    request.Target,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AuthenticationException)
            {
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    private void RecordRequestMetadata(ConnectRequest request)
    {
        if (request.Target.Contains("://", StringComparison.Ordinal)
            || request.Target.Contains('?', StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _absoluteUriRequestCount);
        }

        if (request.Headers.Contains("Cookie"))
        {
            Interlocked.Increment(ref _cookieHeaderCount);
        }

        if (request.Headers.Contains("Proxy-Authorization"))
        {
            Interlocked.Increment(ref _proxyAuthorizationHeaderCount);
        }
    }

    private static async Task TunnelAsync(
        NetworkStream clientStream,
        string authority,
        CancellationToken cancellationToken)
    {
        if (!TryParseAuthority(authority, out var host, out var port))
        {
            await WriteResponseAsync(
                clientStream,
                "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using var target = new TcpClient();
        await target.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var targetStream = target.GetStream();
        await using var targetStreamLifetime = targetStream.ConfigureAwait(false);
        await WriteResponseAsync(
            clientStream,
            "HTTP/1.1 200 Connection Established\r\n\r\n",
            cancellationToken).ConfigureAwait(false);

        await CopyTunnelAsync(
            clientStream,
            targetStream,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyTunnelAsync(
        Stream clientStream,
        Stream targetStream,
        CancellationToken cancellationToken)
    {
        using var tunnelCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var clientToTarget = clientStream.CopyToAsync(
            targetStream,
            tunnelCancellation.Token);
        var targetToClient = targetStream.CopyToAsync(
            clientStream,
            tunnelCancellation.Token);
        await Task.WhenAny(clientToTarget, targetToClient).ConfigureAwait(false);
        await tunnelCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(clientToTarget, targetToClient).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (tunnelCancellation.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task InterceptAsync(
        NetworkStream clientStream,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        await WriteResponseAsync(
            clientStream,
            "HTTP/1.1 200 Connection Established\r\n\r\n",
            cancellationToken).ConfigureAwait(false);
        var tlsStream = new SslStream(clientStream, leaveInnerStreamOpen: true);
        await using var tlsStreamLifetime = tlsStream.ConfigureAwait(false);
        await tlsStream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.None
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ConnectRequest?> ReadConnectRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < MaximumHeaderBytes)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return null;
            }

            bytes.Add(buffer[0]);
            var length = bytes.Count;
            if (length >= 4
                && bytes[length - 4] == '\r'
                && bytes[length - 3] == '\n'
                && bytes[length - 2] == '\r'
                && bytes[length - 1] == '\n')
            {
                break;
            }
        }

        if (bytes.Count >= MaximumHeaderBytes)
        {
            throw new InvalidDataException("The CONNECT request header exceeded the test limit.");
        }

        var lines = Encoding.ASCII.GetString(bytes.ToArray())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2)
        {
            return null;
        }

        var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                headerNames.Add(line[..separator].Trim());
            }
        }

        return new ConnectRequest(requestParts[0], requestParts[1], headerNames);
    }

    private static bool TryParseAuthority(
        string authority,
        out string host,
        out int port)
    {
        host = string.Empty;
        port = 0;
        if (!Uri.TryCreate($"http://{authority}", UriKind.Absolute, out var uri)
            || uri.Port is < 1 or > 65535)
        {
            return false;
        }

        host = uri.IdnHost;
        port = uri.Port;
        return true;
    }

    private static Task WriteResponseAsync(
        Stream stream,
        string response,
        CancellationToken cancellationToken)
    {
        return stream.WriteAsync(
            Encoding.ASCII.GetBytes(response),
            cancellationToken).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _listener.Dispose();
            _shutdown.Dispose();
        }
    }

    private sealed record ConnectRequest(
        string Method,
        string Target,
        IReadOnlySet<string> Headers);
}
