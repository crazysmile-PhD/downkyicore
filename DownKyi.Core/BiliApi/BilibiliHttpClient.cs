using System.Net;
using System.Net.Http.Headers;

namespace DownKyi.Core.BiliApi;

public enum BilibiliHttpFailureKind
{
    Authentication,
    RateLimited,
    HttpStatus,
    EmptyResponse,
    Transport
}

public sealed class BilibiliHttpRequestException : HttpRequestException
{
    public BilibiliHttpRequestException()
        : this("A Bilibili HTTP request failed.", BilibiliHttpFailureKind.Transport)
    {
    }

    public BilibiliHttpRequestException(string message)
        : this(message, BilibiliHttpFailureKind.Transport)
    {
    }

    public BilibiliHttpRequestException(string message, Exception innerException)
        : this(message, BilibiliHttpFailureKind.Transport, innerException: innerException)
    {
    }

    public BilibiliHttpRequestException(
        string message,
        BilibiliHttpFailureKind failureKind,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException, statusCode)
    {
        FailureKind = failureKind;
    }

    public BilibiliHttpFailureKind FailureKind { get; }
}

public sealed class BilibiliHttpClient
{
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly Action<TimeSpan, CancellationToken> _delay;

    public BilibiliHttpClient(HttpClient httpClient)
        : this(httpClient, static (delay, cancellationToken) =>
        {
            if (cancellationToken.WaitHandle.WaitOne(delay))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        })
    {
    }

    internal BilibiliHttpClient(
        HttpClient httpClient,
        Action<TimeSpan, CancellationToken> delay)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    internal string Send(
        Func<HttpRequestMessage> requestFactory,
        int attempts,
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? sendOverride,
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
                using var response = sendOverride?.Invoke(request, cancellationToken)
                                     ?? _httpClient.Send(
                                         request,
                                         HttpCompletionOption.ResponseHeadersRead,
                                         cancellationToken);
                var retryDelay = GetRetryDelay(response, attempt);
                if (retryDelay != null && attempt < attempts)
                {
                    WaitForRetry(retryDelay.Value, cancellationToken);
                    continue;
                }

                ThrowForTerminalStatus(response);
                using var stream = response.Content.ReadAsStream(cancellationToken);
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new BilibiliHttpRequestException(
                        "Bilibili returned an empty response.",
                        BilibiliHttpFailureKind.EmptyResponse,
                        response.StatusCode);
                }

                return content;
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

                WaitForRetry(GetBackoff(attempt), cancellationToken);
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

    internal HttpResponseMessage SendResponse(
        HttpRequestMessage request,
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? sendOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return sendOverride?.Invoke(request, cancellationToken)
               ?? _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static bool IsRetryableException(Exception exception)
    {
        return exception is not BilibiliHttpRequestException
        {
            FailureKind: BilibiliHttpFailureKind.Authentication or BilibiliHttpFailureKind.HttpStatus
        };
    }

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return GetRetryAfter(response.Headers.RetryAfter) ?? GetBackoff(attempt);
        }

        return (int)response.StatusCode >= 500 ? GetBackoff(attempt) : null;
    }

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return ClampDelay(delta);
        }

        if (retryAfter?.Date is { } date)
        {
            return ClampDelay(date - DateTimeOffset.UtcNow);
        }

        return null;
    }

    private static void ThrowForTerminalStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
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

    private void WaitForRetry(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        _delay(delay, cancellationToken);
    }

    internal static TimeSpan GetBackoff(int attempt)
    {
        return TimeSpan.FromMilliseconds(Math.Clamp(attempt * 250, 250, 2000));
    }

    private static TimeSpan ClampDelay(TimeSpan delay)
    {
        return delay <= TimeSpan.Zero
            ? TimeSpan.Zero
            : delay > MaximumRetryDelay
                ? MaximumRetryDelay
                : delay;
    }
}
