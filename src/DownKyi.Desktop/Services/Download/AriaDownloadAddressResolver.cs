using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Download;

internal sealed class AriaDownloadAddressResolver : IDisposable
{
    private const int MaximumRedirects = 5;
    private readonly HttpMessageInvoker _http;

    private AriaDownloadAddressResolver(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _http = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public static AriaDownloadAddressResolver Create(Uri? proxyAddress)
    {
        if (proxyAddress is { Scheme: not "http" })
        {
            throw new ArgumentException(
                "The aria2 HTTPS download proxy must be an HTTP CONNECT endpoint.",
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

            var resolver = new AriaDownloadAddressResolver(handler);
            handler = null;
            return resolver;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    internal static AriaDownloadAddressResolver CreateForTest(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        HttpMessageHandler? ownedHandler = handler;
        try
        {
            var resolver = new AriaDownloadAddressResolver(ownedHandler);
            ownedHandler = null;
            return resolver;
        }
        finally
        {
            ownedHandler?.Dispose();
        }
    }

    public async Task<AriaDownloadAddressResolution> ResolveAsync(
        string address,
        string userAgent,
        string? credentials,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var current)
            || !string.Equals(current.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return AriaDownloadAddressResolution.Rejected(
                "download.transfer.insecure-address");
        }

        for (var redirectCount = 0; ; redirectCount++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskHeaders = AriaTaskHeaderPolicy.Create(
                current.AbsoluteUri,
                userAgent,
                credentials);
            using var request = CreateProbeRequest(current, taskHeaders);
            using var response = await _http.SendAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                return AriaDownloadAddressResolution.Accepted(
                    current.AbsoluteUri,
                    taskHeaders);
            }

            if (redirectCount >= MaximumRedirects)
            {
                return AriaDownloadAddressResolution.Rejected(
                    "download.transfer.redirect-limit");
            }

            var redirect = ResolveRedirect(current, response.Headers.Location);
            if (redirect.ErrorCode != null)
            {
                return redirect;
            }

            var next = redirect.Address
                ?? throw new InvalidOperationException(
                    "The accepted aria2 redirect address is missing.");
            if (taskHeaders.CarriesCredentials && !IsSameOrigin(current, next))
            {
                return AriaDownloadAddressResolution.Rejected(
                    "download.transfer.credentialed-redirect");
            }

            current = next;
        }
    }

    private static HttpRequestMessage CreateProbeRequest(
        Uri address,
        AriaTaskHeaders taskHeaders)
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
                throw new InvalidOperationException("An aria2 task header is malformed.");
            }

            request.Headers.TryAddWithoutValidation(
                header[..separator],
                header[(separator + 1)..].TrimStart());
        }

        return request;
    }

    private static AriaDownloadAddressResolution ResolveRedirect(
        Uri current,
        Uri? location)
    {
        if (location == null)
        {
            return AriaDownloadAddressResolution.Rejected(
                "download.transfer.redirect-location-missing");
        }

        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return AriaDownloadAddressResolution.Rejected(
                "download.transfer.insecure-redirect");
        }

        return AriaDownloadAddressResolution.Redirect(next);
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
}

internal sealed record AriaDownloadAddressResolution(
    Uri? Address,
    AriaTaskHeaders? Headers,
    string? ErrorCode)
{
    public static AriaDownloadAddressResolution Accepted(
        string address,
        AriaTaskHeaders headers) =>
        new(new Uri(address, UriKind.Absolute), headers, ErrorCode: null);

    public static AriaDownloadAddressResolution Redirect(Uri address) =>
        new(address, Headers: null, ErrorCode: null);

    public static AriaDownloadAddressResolution Rejected(string errorCode) =>
        new(Address: null, Headers: null, errorCode);
}
