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

    internal Task<BilibiliHttpTextResponse> GetResponseAsync(
        Func<HttpRequestMessage> requestFactory,
        int attempts,
        bool requireContent,
        bool allowRedirectStatus,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            requestFactory,
            attempts,
            async (response, token) =>
            {
                var content = await response.Content.ReadAsStringAsync(token)
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
            },
            allowRedirectStatus,
            cancellationToken);
    }

    public Task<Stream> OpenReadAsync(
        Func<HttpRequestMessage> requestFactory,
        int attempts,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync<Stream>(
            requestFactory,
            attempts,
            static async (response, token) =>
            {
                var content = response.Content;
                var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
                // Transfer the content to the stream; the attempt still owns request/response.
                response.Content = null;
                return new ResponseStream(stream, content);
            },
            allowRedirectStatus: false,
            cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        int attempts,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readResponseAsync,
        bool allowRedirectStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan retryDelay;
            try
            {
                // ResponseHeadersRead does not apply HttpClient.Timeout to the response body.
                using var timeout = new CancellationTokenSource(
                    BilibiliServiceCollectionExtensions.RequestTimeout,
                    _timeProvider);
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
                var attemptToken = attemptCancellation.Token;
                using var request = requestFactory();
                var client = _httpClientFactory.CreateClient(BilibiliServiceCollectionExtensions.HttpClientName);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptToken).ConfigureAwait(false);
                if (GetRetryDelay(response, attempt) is { } delay && attempt < attempts)
                {
                    retryDelay = delay;
                }
                else
                {
                    ThrowForTerminalStatus(response, allowRedirectStatus);
                    return await readResponseAsync(response, attemptToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                or InvalidOperationException
                || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastError = exception is OperationCanceledException
                    ? new BilibiliHttpRequestException(
                        "Bilibili request timed out.",
                        BilibiliHttpFailureKind.Timeout,
                        innerException: exception)
                    : exception;
                if (!IsRetryableException(lastError) || attempt == attempts)
                {
                    break;
                }

                retryDelay = GetBackoff(attempt);
            }

            await DelayAsync(retryDelay, cancellationToken).ConfigureAwait(false);
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

    internal static TimeSpan GetBackoff(int attempt)
    {
        return TimeSpan.FromMilliseconds(Math.Clamp(attempt * 250, 250, 2000));
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
        private int _disposed;

        public ResponseStream(
            Stream inner,
            HttpContent content)
        {
            // HttpContent is the sole owner of the cached response stream.
            _getInner = () => inner;
            _content = content;
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
                _content.Dispose();
            }
        }
    }
}
