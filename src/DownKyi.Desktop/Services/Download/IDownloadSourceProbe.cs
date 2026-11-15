using System;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Download;

internal interface IDownloadSourceProbe
{
    Task<DownloadSourceObservation> ProbeAsync(string address, CancellationToken cancellationToken);
}

internal abstract record DownloadSourceObservation;

internal sealed record DownloadSourceReady(
    TimeSpan Elapsed, int Bytes, bool SupportsRanges, bool HasValidator) : DownloadSourceObservation;

internal sealed record DownloadSourceUnavailable : DownloadSourceObservation;
