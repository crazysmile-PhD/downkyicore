using System.Net;
using System.Net.Http.Headers;
using DownKyi.Application.Bilibili;

namespace DownKyi.Infrastructure.Bilibili;

internal sealed record BilibiliHttpTextResponse(
    string Content,
    IReadOnlyList<string> SetCookieHeaders,
    HttpStatusCode StatusCode,
    Uri? Location);

internal sealed class BilibiliHttpTransport
{
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public BilibiliHttpTransport(
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider)
        : this(
            httpClientFactory,
            timeProvider,
            (delay, cancellationToken) =>
                Task.Delay(delay, timeProvider, cancellationToken))
    {
    }

    internal BilibiliHttpTransport(
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _httpClientFactory = httpClientFactory
                             ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public async Task<string> GetStringAsync(
        Func<HttpRequestMessage> requestFactory,
        int attempts,
        CancellationToken cancellationToken)
    {
        var response = await GetResponseAsync(
            requestFactory,
            attempts,
            requireContent: true,
            allowRedirectStatus: false,
            cancellationToken).ConfigureAwait(false);
        return response.Content;
    }

    internal async Task<BilibiliHttpTextResponse> GetResponseAsync(
        Func<HttpRequestMessage> requestFactory,
        int attempts,
        bool requireContent,
        bool allowRedirectStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = requestFactory();
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (await RetryResponseAsync(response, attempt, attempts, cancellationToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                ThrowForTerminalStatus(response, allowRedirectStatus);
                var content = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (requireContent && string.IsNullOrWhiteSpace(content))
                {
                    throw new BilibiliHttpRequestException(
                        "Bilibili returned an empty response.",
                        BilibiliHttpFailureKind.EmptyResponse,
                        response.StatusCode);
                }

                var setCookieHeaders = response.Headers.TryGetValues(
                    "Set-Cookie",
                    out var values)
                    ? values.ToArray()
                    : [];
                return new BilibiliHttpTextResponse(
                    content,
                    setCookieHeaders,
                    response.StatusCode,
                    response.Headers.Location);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                or InvalidOperationException)
            {
                lastError = exception;
                if (!IsRetryableException(exception) || attempt == attempts)
                {
                    break;
                }

                await DelayAsync(GetBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastError is BilibiliHttpRequestException typedFailure)
        {
            throw typedFailure;
        }

        throw new BilibiliHttpRequestException(
            $"Bilibili request failed after {attempts} attempts.",
            BilibiliHttpFailureKind.Transport,
            innerException: lastError);
    }

    public async Task<Stream> OpenReadAsync(
        Func<HttpRequestMessage> requestFactory,
        int attempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpRequestMessage? request = null;
            HttpResponseMessage? response = null;
            try
            {
                request = requestFactory();
                response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (await RetryResponseAsync(response, attempt, attempts, cancellationToken)
                    .ConfigureAwait(false))
                {
                    response.Dispose();
                    request.Dispose();
                    continue;
                }

                ThrowForTerminalStatus(response);
                var content = response.Content;
                var stream = await content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                response.Content = null;
                response.Dispose();
                response = null;
                var responseStream = new ResponseStream(stream, content, request);
                request = null;
                return responseStream;
            }
            catch (OperationCanceledException)
            {
                response?.Dispose();
                request?.Dispose();
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                or InvalidOperationException)
            {
                response?.Dispose();
                request?.Dispose();
                lastError = exception;
                if (!IsRetryableException(exception) || attempt == attempts)
                {
                    break;
                }

                await DelayAsync(GetBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastError is BilibiliHttpRequestException typedFailure)
        {
            throw typedFailure;
        }

        throw new BilibiliHttpRequestException(
            $"Bilibili stream request failed after {attempts} attempts.",
            BilibiliHttpFailureKind.Transport,
            innerException: lastError);
    }

    internal static TimeSpan GetBackoff(int attempt)
    {
        return TimeSpan.FromMilliseconds(Math.Clamp(attempt * 250, 250, 2000));
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(BilibiliServiceCollectionExtensions.HttpClientName);
        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RetryResponseAsync(
        HttpResponseMessage response,
        int attempt,
        int attempts,
        CancellationToken cancellationToken)
    {
        var retryDelay = GetRetryDelay(response, attempt);
        if (retryDelay == null || attempt >= attempts)
        {
            return false;
        }

        await DelayAsync(retryDelay.Value, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : _delayAsync(delay, cancellationToken);
    }

    private static bool IsRetryableException(Exception exception)
    {
        return exception is not BilibiliHttpRequestException
        {
            FailureKind: BilibiliHttpFailureKind.Authentication or BilibiliHttpFailureKind.HttpStatus
        };
    }

    private TimeSpan? GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return GetRetryAfter(response.Headers.RetryAfter) ?? GetBackoff(attempt);
        }

        return (int)response.StatusCode >= 500 ? GetBackoff(attempt) : null;
    }

    private TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return ClampDelay(delta);
        }

        if (retryAfter?.Date is { } date)
        {
            return ClampDelay(date - _timeProvider.GetUtcNow());
        }

        return null;
    }

    private static void ThrowForTerminalStatus(
        HttpResponseMessage response,
        bool allowRedirectStatus = false)
    {
        if (response.IsSuccessStatusCode
            || (allowRedirectStatus && (int)response.StatusCode is >= 300 and < 400))
        {
            return;
        }

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                BilibiliHttpFailureKind.Authentication,
            HttpStatusCode.TooManyRequests => BilibiliHttpFailureKind.RateLimited,
            _ => BilibiliHttpFailureKind.HttpStatus
        };
        throw new BilibiliHttpRequestException(
            $"Bilibili returned HTTP {(int)response.StatusCode}.",
            kind,
            response.StatusCode);
    }

    private static TimeSpan ClampDelay(TimeSpan delay)
    {
        return delay <= TimeSpan.Zero
            ? TimeSpan.Zero
            : delay > MaximumRetryDelay
                ? MaximumRetryDelay
                : delay;
    }

    private sealed class ResponseStream : Stream
    {
        private readonly Func<Stream> _getInner;
        private readonly HttpContent _content;
        private readonly HttpRequestMessage _request;
        private int _disposed;

        public ResponseStream(
            Stream inner,
            HttpContent content,
            HttpRequestMessage request)
        {
            // HttpContent is the sole owner of the cached response stream.
            _getInner = () => inner;
            _content = content;
            _request = request;
        }

        private Stream Inner => _getInner();

        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => Inner.CanSeek;
        public override bool CanWrite => Inner.CanWrite;
        public override long Length => Inner.Length;

        public override long Position
        {
            get => Inner.Position;
            set => Inner.Position = value;
        }

        public override void Flush() => Inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            Inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            Inner.Seek(offset, origin);

        public override void SetLength(long value) => Inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            Inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeResources();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeResources();
            return base.DisposeAsync();
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _content.Dispose();
                }
                finally
                {
                    _request.Dispose();
                }
            }
        }
    }
}
