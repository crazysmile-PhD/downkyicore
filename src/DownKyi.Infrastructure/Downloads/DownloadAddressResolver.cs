using System.Net;
using System.Net.Http.Headers;

namespace DownKyi.Infrastructure.Downloads;

public interface IDownloadAddressResolver : IDisposable
{
    Task<DownloadAddressResolution> ResolveAsync(
        string address,
        string userAgent,
        string? credentials,
        CancellationToken cancellationToken);
}

public sealed class DownloadAddressResolver : IDownloadAddressResolver
{
    private const string BilibiliHost = "bilibili.com";
    private const int MaximumRedirects = 5;
    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpMessageInvoker _http;
    private readonly TimeSpan _probeTimeout;

    internal DownloadAddressResolver(
        HttpMessageHandler handler,
        TimeSpan? probeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        if (_probeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probeTimeout),
                _probeTimeout,
                "The download address probe timeout must be positive.");
        }

        _http = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public static DownloadAddressResolver Create(Uri? proxyAddress)
    {
        if (proxyAddress is { Scheme: not "http" })
        {
            throw new ArgumentException(
                "The HTTPS download proxy must be an HTTP CONNECT endpoint.",
                nameof(proxyAddress));
        }

        SocketsHttpHandler? handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            UseProxy = proxyAddress != null
        };
        try
        {
            if (proxyAddress != null)
            {
                handler.Proxy = new WebProxy(proxyAddress)
                {
                    BypassProxyOnLocal = false
                };
            }

            var resolver = new DownloadAddressResolver(handler);
            handler = null;
            return resolver;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    public async Task<DownloadAddressResolution> ResolveAsync(
        string address,
        string userAgent,
        string? credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var current)
            || !string.Equals(current.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new DownloadAddressCandidateRejected(
                "download.transfer.insecure-address");
        }

        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        probeCancellation.CancelAfter(_probeTimeout);
        for (var redirectCount = 0; ; redirectCount++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskHeaders = CreateHeaders(
                current,
                userAgent,
                credentials);
            using var request = CreateProbeRequest(current, taskHeaders);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(
                    request,
                    probeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DownloadAddressTimedOut();
            }
            catch (TimeoutException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new DownloadAddressTimedOut();
            }
            catch (HttpRequestException exception) when (
                TlsFailureClassifier.TryClassify(exception, out var tlsErrorCode))
            {
                return new DownloadAddressCandidateRejected(tlsErrorCode);
            }
            catch (HttpRequestException)
            {
                return new DownloadAddressTransportFailure();
            }
            catch (IOException)
            {
                return new DownloadAddressTransportFailure();
            }

            using (response)
            {
                if (!IsRedirect(response.StatusCode))
                {
                    return new DownloadAddressAccepted(
                        current,
                        taskHeaders);
                }

                if (redirectCount >= MaximumRedirects)
                {
                    return new DownloadAddressCandidateRejected(
                        "download.transfer.redirect-limit");
                }

                var redirect = ResolveRedirect(current, response.Headers.Location);
                if (redirect.ErrorCode != null)
                {
                    return new DownloadAddressCandidateRejected(
                        redirect.ErrorCode);
                }

                var next = redirect.Address
                    ?? throw new InvalidOperationException(
                        "The accepted download redirect address is missing.");
                if (taskHeaders.CarriesCredentials && !IsSameOrigin(current, next))
                {
                    return new DownloadAddressCandidateRejected(
                        "download.transfer.credentialed-redirect");
                }

                current = next;
            }
        }
    }

    private static DownloadRequestHeaders CreateHeaders(
        Uri address,
        string userAgent,
        string? credentials)
    {
        ValidateHeaderValue(userAgent, nameof(userAgent));
        var headers = new List<string>
        {
            "Origin: https://www.bilibili.com",
            "Referer: https://www.bilibili.com"
        };
        var carriesCredentials = string.Equals(address.IdnHost, BilibiliHost, StringComparison.OrdinalIgnoreCase)
                                 || address.IdnHost.EndsWith("." + BilibiliHost, StringComparison.OrdinalIgnoreCase);
        if (carriesCredentials)
        {
            if (!string.IsNullOrWhiteSpace(credentials))
            {
                ValidateHeaderValue(credentials, "credentials");
                headers.Add($"Cookie: {credentials}");
            }
            else
            {
                carriesCredentials = false;
            }
        }

        return new DownloadRequestHeaders(headers, userAgent, carriesCredentials);
    }

    private static void ValidateHeaderValue(string value, string parameterName)
    {
        if (value.Any(character => character is '\r' or '\n' or '\0'
            || char.IsControl(character)))
        {
            throw new ArgumentException(
                "A download request header contains a control character.",
                parameterName);
        }
    }

    private static HttpRequestMessage CreateProbeRequest(
        Uri address,
        DownloadRequestHeaders taskHeaders)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        request.Headers.TryAddWithoutValidation("User-Agent", taskHeaders.UserAgent);
        foreach (var header in taskHeaders.Headers)
        {
            var separator = header.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                request.Dispose();
                throw new InvalidOperationException("A download request header is malformed.");
            }

            request.Headers.TryAddWithoutValidation(
                header[..separator],
                header[(separator + 1)..].TrimStart());
        }

        return request;
    }

    private static RedirectResolution ResolveRedirect(
        Uri current,
        Uri? location)
    {
        if (location == null)
        {
            return new RedirectResolution(
                Address: null,
                ErrorCode: "download.transfer.redirect-location-missing");
        }

        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new RedirectResolution(
                Address: null,
                ErrorCode: "download.transfer.insecure-redirect");
        }

        return new RedirectResolution(next, ErrorCode: null);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static bool IsSameOrigin(Uri first, Uri second)
    {
        return string.Equals(
                   first.Scheme,
                   second.Scheme,
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   first.IdnHost,
                   second.IdnHost,
                   StringComparison.OrdinalIgnoreCase)
               && first.Port == second.Port;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private sealed record RedirectResolution(Uri? Address, string? ErrorCode);
}

public abstract record DownloadAddressResolution
{
    private protected DownloadAddressResolution()
    {
    }
}

public sealed record DownloadAddressAccepted(
    Uri Address,
    DownloadRequestHeaders Headers) : DownloadAddressResolution;

public sealed record DownloadAddressCandidateRejected(
    string ErrorCode) : DownloadAddressResolution;

public sealed record DownloadAddressTimedOut : DownloadAddressResolution;

public sealed record DownloadAddressTransportFailure : DownloadAddressResolution;

public sealed record DownloadRequestHeaders(
    IReadOnlyList<string> Headers,
    string UserAgent,
    bool CarriesCredentials);
