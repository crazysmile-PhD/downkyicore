using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DownKyi.SystemBenchmarks;

internal sealed class LoopbackRangeServer : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 32 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private readonly byte[] _payloadBlock = new byte[64 * 1024];
    private readonly long _payloadLength;
    private Task? _acceptTask;
    private long _nextHandlerId;

    public LoopbackRangeServer(long payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadLength);
        _payloadLength = payloadLength;
        for (var index = 0; index < _payloadBlock.Length; index++)
        {
            _payloadBlock[index] = checked((byte)(index % 251));
        }
    }

    public Uri Address { get; private set; } = null!;

    public void Start()
    {
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Address = new Uri($"http://127.0.0.1:{endpoint.Port}/payload.bin");
        _acceptTask = AcceptLoopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptTask != null)
        {
            await _acceptTask.ConfigureAwait(false);
        }

        await Task.WhenAll(_handlers.Values).ConfigureAwait(false);
        _listener.Dispose();
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                var identifier = Interlocked.Increment(ref _nextHandlerId);
                var handler = HandleClientAsync(client, _shutdown.Token);
                _handlers[identifier] = handler;
                _ = RemoveCompletedHandlerAsync(identifier, handler);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task RemoveCompletedHandlerAsync(long identifier, Task handler)
    {
        try
        {
            await handler.ConfigureAwait(false);
        }
        finally
        {
            _handlers.TryRemove(identifier, out _);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                await using var streamScope = stream.ConfigureAwait(false);
                var header = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
                var request = ParseRequest(header);
                var start = request.RangeStart ?? 0;
                var end = request.RangeEnd ?? (_payloadLength - 1);
                start = Math.Clamp(start, 0, _payloadLength - 1);
                end = Math.Clamp(end, start, _payloadLength - 1);
                var responseLength = checked(end - start + 1);
                var isPartial = request.RangeStart.HasValue;
                var response = new StringBuilder()
                    .Append(isPartial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n")
                    .Append(CultureInfo.InvariantCulture, $"Content-Length: {responseLength}\r\n")
                    .Append("Accept-Ranges: bytes\r\n")
                    .Append("Content-Type: application/octet-stream\r\n")
                    .Append("Connection: close\r\n");
                if (isPartial)
                {
                    response.Append(
                        CultureInfo.InvariantCulture,
                        $"Content-Range: bytes {start}-{end}/{_payloadLength}\r\n");
                }

                response.Append("\r\n");
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes(response.ToString()),
                    cancellationToken).ConfigureAwait(false);
                if (!request.IsHead)
                {
                    await WritePayloadAsync(stream, responseLength, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task WritePayloadAsync(
        NetworkStream stream,
        long length,
        CancellationToken cancellationToken)
    {
        var remaining = length;
        while (remaining > 0)
        {
            var count = checked((int)Math.Min(_payloadBlock.Length, remaining));
            await stream.WriteAsync(_payloadBlock.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
    }

    private static async Task<string> ReadHeaderAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumHeaderBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (length >= 4
                && buffer[length - 4] == '\r'
                && buffer[length - 3] == '\n'
                && buffer[length - 2] == '\r'
                && buffer[length - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length);
            }
        }

        throw new IOException("Loopback HTTP request header is invalid or too large.");
    }

    private static ParsedRequest ParseRequest(string header)
    {
        var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw new IOException("Loopback HTTP request did not contain a request line.");
        }

        var isHead = lines[0].StartsWith("HEAD ", StringComparison.Ordinal);
        long? rangeStart = null;
        long? rangeEnd = null;
        foreach (var line in lines.Skip(1))
        {
            if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["Range: bytes=".Length..];
            var separator = value.IndexOf('-', StringComparison.Ordinal);
            if (separator <= 0
                || !long.TryParse(value[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            {
                throw new IOException("Loopback HTTP range is invalid.");
            }

            rangeStart = start;
            if (separator < value.Length - 1
                && long.TryParse(value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var end))
            {
                rangeEnd = end;
            }
        }

        return new ParsedRequest(isHead, rangeStart, rangeEnd);
    }

    private sealed record ParsedRequest(bool IsHead, long? RangeStart, long? RangeEnd);
}
