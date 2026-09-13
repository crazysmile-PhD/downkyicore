using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DownKyi.TestInfrastructure;

public sealed record LoopbackResponse(
    HttpStatusCode StatusCode,
    string Body = "",
    TimeSpan DelayBeforeResponse = default,
    long? ContentLength = null,
    int? BytesToSend = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    ReadOnlyMemory<byte>? BodyBytes = null);

public sealed record LoopbackHttpRequest(
    int RequestNumber,
    string Method,
    string PathAndQuery,
    long? RangeStart);

public sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TaskCompletionSource _firstRequestReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<LoopbackHttpRequest, LoopbackResponse> _responseFactory;
    private readonly ConcurrentQueue<LoopbackHttpRequest> _requests = new();
    private readonly TcpListener _listener;
    private readonly Task _serverTask;
    private int _requestCount;

    public LoopbackHttpServer(Func<int, LoopbackResponse> responseFactory)
        : this(request => responseFactory(request.RequestNumber))
    {
        ArgumentNullException.ThrowIfNull(responseFactory);
    }

    private LoopbackHttpServer(Func<LoopbackHttpRequest, LoopbackResponse> responseFactory)
    {
        _responseFactory = responseFactory ?? throw new ArgumentNullException(nameof(responseFactory));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Url = new Uri($"http://127.0.0.1:{endpoint.Port}/api");
        _serverTask = RunAsync(_shutdown.Token);
    }

    public Uri Url { get; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    public IReadOnlyCollection<LoopbackHttpRequest> Requests => _requests.ToArray();

    public static LoopbackHttpServer CreateRequestAware(
        Func<LoopbackHttpRequest, LoopbackResponse> responseFactory)
    {
        return new LoopbackHttpServer(responseFactory);
    }

    public Task WaitForFirstRequestAsync(CancellationToken cancellationToken)
    {
        return _firstRequestReceived.Task.WaitAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        await using var streamLifetime = stream.ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? string.Empty;
        var requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var method = requestParts.Length > 0 ? requestParts[0] : string.Empty;
        var pathAndQuery = requestParts.Length > 1 ? requestParts[1] : string.Empty;
        long? rangeStart = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } line)
        {
            const string rangePrefix = "Range: bytes=";
            if (line.StartsWith(rangePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rangeValue = line[rangePrefix.Length..];
                var separator = rangeValue.IndexOf('-', StringComparison.Ordinal);
                var startText = separator >= 0 ? rangeValue[..separator] : rangeValue;
                if (long.TryParse(startText, out var parsedRangeStart))
                {
                    rangeStart = parsedRangeStart;
                }
            }
        }

        var requestNumber = Interlocked.Increment(ref _requestCount);
        var request = new LoopbackHttpRequest(
            requestNumber,
            method,
            pathAndQuery,
            rangeStart);
        _requests.Enqueue(request);
        _firstRequestReceived.TrySetResult();
        var response = _responseFactory(request);
        if (response.DelayBeforeResponse > TimeSpan.Zero)
        {
            await Task.Delay(response.DelayBeforeResponse, cancellationToken)
                .ConfigureAwait(false);
        }

        var body = response.BodyBytes ?? new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(response.Body));
        var contentLength = response.ContentLength ?? body.Length;
        var reason = response.StatusCode switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.PartialContent => "Partial Content",
            HttpStatusCode.Forbidden => "Forbidden",
            HttpStatusCode.TooManyRequests => "Too Many Requests",
            HttpStatusCode.InternalServerError => "Internal Server Error",
            _ => response.StatusCode.ToString()
        };

        var headers = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append((int)response.StatusCode)
            .Append(' ')
            .Append(reason)
            .Append("\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: ")
            .Append(contentLength)
            .Append("\r\nConnection: close\r\n");

        if (response.Headers != null)
        {
            foreach (var header in response.Headers)
            {
                headers.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
        }

        headers.Append("\r\n");
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(headers.ToString()),
            cancellationToken).ConfigureAwait(false);

        var bytesToSend = Math.Clamp(response.BytesToSend ?? body.Length, 0, body.Length);
        if (bytesToSend > 0)
        {
            await stream.WriteAsync(
                body[..bytesToSend],
                cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

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
}
