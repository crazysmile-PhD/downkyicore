using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Utils;

namespace DownKyi.Services.Download;

internal sealed partial class BuiltinTransferBackend
{
    // A bounded read, not a HEAD request: quick headers cannot hide a drip-feed body.
    public async Task<DownloadSourceObservation> ProbeAsync(
        string address, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3), _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = _clock.GetTimestamp();
        try
        {
            var network = _settingsStore.Current.Network;
            var proxy = ResolveProxyAddress(network);
            using var resolver = _addressResolverFactory(proxy);
            var resolution = await resolver.ResolveAsync(address, network.UserAgent,
                LoginHelper.GetLoginInfoCookiesString(), deadline.Token).ConfigureAwait(false);
            if (resolution is not DownloadAddressAccepted accepted)
            {
                return new DownloadSourceUnavailable();
            }

            using var handler = CreateHttpHandler(network, proxy);
            using var http = new HttpMessageInvoker(handler, disposeHandler: false);
            using var request = new HttpRequestMessage(HttpMethod.Get, accepted.Address);
            request.Headers.Range = new RangeHeaderValue(0, 65535);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            request.Headers.TryAddWithoutValidation("User-Agent", accepted.Headers.UserAgent);
            foreach (var header in accepted.Headers.Headers)
            {
                var separator = header.IndexOf(':', StringComparison.Ordinal);
                request.Headers.TryAddWithoutValidation(header[..separator], header[(separator + 1)..].TrimStart());
            }

            using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var ranges = response.StatusCode == HttpStatusCode.PartialContent;
            if ((!ranges && response.StatusCode != HttpStatusCode.OK) ||
                (ranges && response.Content.Headers.ContentRange is not { From: 0, Length: > 0 }))
            {
                return new DownloadSourceUnavailable();
            }

            var buffer = new byte[65536];
            var bytes = 0;
            var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using var sourceLifetime = source.ConfigureAwait(false);
            while (bytes < buffer.Length)
            {
                var read = await source.ReadAsync(buffer.AsMemory(bytes), deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytes += read;
            }

            return bytes == 0 ? new DownloadSourceUnavailable() : new DownloadSourceReady(
                _clock.GetElapsedTime(started), bytes, ranges,
                BuiltinRangeResourceIdentity.Create(accepted.Address, response).HasContinuityValidator);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DownloadSourceUnavailable();
        }
        catch (Exception exception) when (exception is HttpRequestException or System.IO.IOException or TimeoutException)
        {
            return new DownloadSourceUnavailable();
        }
    }
}
