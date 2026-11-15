using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

/// <summary>
/// Downloads one resolved address with bounded parallel HTTP range workers. The range
/// segmentation, worker queue, resumable per-range state and read-stall watchdog are adapted
/// from BBDown's MIT-licensed download pipeline. See THIRD-PARTY-NOTICES.md.
/// </summary>
internal sealed class BuiltinRangeDownloader : IDisposable
{
    internal const long DefaultSegmentSize = 20L * 1024 * 1024;
    internal static readonly TimeSpan DefaultReadStallTimeout = TimeSpan.FromSeconds(60);

    private const int BufferSize = 256 * 1024;
    private const int MaximumResumeMetadataBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions ResumeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Uri _address;
    private readonly HttpMessageInvoker _http;
    private readonly DownloadRequestHeaders _headers;
    private readonly ILogger _logger;
    private readonly Action<int>? _planCreated;
    private readonly Action<bool>? _networkReadStateChanged;
    private readonly Action<int>? _networkBytesReceived;
    private readonly int _parallelCount;
    private readonly Action<long, long> _progress;
    private readonly TimeSpan _readStallTimeout;
    private readonly long _segmentSize;
    private readonly DownloadBandwidthLimiter.TransferLease? _bandwidthLimiter;
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private readonly SemaphoreSlim _hedgeGate = new(1, 1);
    private readonly TimeSpan _hedgeDelay;
    private long _referenceBytesPerSecond;
    private bool _disposed;

    public BuiltinRangeDownloader(
        HttpMessageHandler handler,
        Uri address,
        DownloadRequestHeaders headers,
        int parallelCount,
        Action<long, long> progress,
        ILogger logger,
        long segmentSize = DefaultSegmentSize,
        TimeSpan? readStallTimeout = null,
        bool disposeHandler = true,
        DownloadBandwidthLimiter.TransferLease? bandwidthLimiter = null,
        Action<int>? planCreated = null,
        Action<bool>? networkReadStateChanged = null,
        TimeSpan? hedgeDelay = null,
        Action<int>? networkBytesReceived = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _headers = headers ?? throw new ArgumentNullException(nameof(headers));
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentSize, 1);
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _planCreated = planCreated;
        _networkReadStateChanged = networkReadStateChanged;
        _networkBytesReceived = networkBytesReceived;
        _hedgeDelay = hedgeDelay ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_hedgeDelay, TimeSpan.Zero);
        _parallelCount = parallelCount;
        _segmentSize = segmentSize;
        _bandwidthLimiter = bandwidthLimiter;
        _readStallTimeout = readStallTimeout ?? DefaultReadStallTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _readStallTimeout,
            TimeSpan.Zero);

        _http = new HttpMessageInvoker(handler, disposeHandler);
    }

    public async Task<BuiltinRangeDownloadResult> DownloadAsync(
        string targetFile,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFile);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var recovered = TryRecoverCompletedTarget(targetFile, expectedBytes);
        if (recovered != null)
        {
            return recovered;
        }

        var sequentialExpectedBytes = expectedBytes;
        try
        {
            var probe = await ProbeAsync(expectedBytes, cancellationToken).ConfigureAwait(false);
            sequentialExpectedBytes = probe.TotalBytes;
            return await DownloadCoreAsync(
                targetFile,
                probe,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BuiltinRangeProtocolException exception) when (!File.Exists(targetFile))
        {
            _logger.LogWarningMessage(
                $"Built-in range transfer was rejected by the media server; " +
                $"fallback=sequential; reason={exception.Message}");
            return await DownloadSequentialAsync(
                targetFile,
                sequentialExpectedBytes,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<BuiltinRangeDownloadResult> DownloadCoreAsync(
        string targetFile,
        BuiltinRangeProbe probe,
        CancellationToken cancellationToken)
    {
        var recovered = TryRecoverCompletedTarget(targetFile, probe.TotalBytes);
        if (recovered != null)
        {
            return recovered;
        }

        var partialFile = $"{targetFile}.download";
        var state = await LoadOrCreateStateAsync(
            partialFile,
            probe.TotalBytes,
            probe.SupportsRanges,
            probe.ResourceIdentity,
            cancellationToken).ConfigureAwait(false);
        _planCreated?.Invoke(state.Chunks.Length);
        _logger.LogInformationMessage(
            $"Built-in range plan; ranges={probe.SupportsRanges}; " +
            $"validator={probe.ResourceIdentity.HasContinuityValidator}; " +
            $"workers={Math.Min(_parallelCount, state.Chunks.Count(static chunk => !chunk.IsComplete))}; " +
            $"retainedBytes={state.Chunks.Sum(static chunk => chunk.Position)}.");
        var receivedBytes = state.Chunks.Sum(static chunk => chunk.Position);
        _progress(receivedBytes, probe.TotalBytes);

        var storage = new FileStream(
            partialFile,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using var storageLifetime = storage.ConfigureAwait(false);
        Exception? primaryFailure = null;
        try
        {
            receivedBytes = await DownloadWithCheckpointsAsync(
                storage,
                state,
                probe.SupportsRanges,
                receivedBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            try
            {
                await SaveCheckpointAsync(storage, state, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception checkpointException) when (checkpointException is IOException
                or UnauthorizedAccessException)
            {
                _logger.LogWarningMessage(
                    $"Built-in range checkpoint failed while preserving the primary " +
                    $"transfer failure; type={checkpointException.GetType().Name}.");
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }

        if (state.Chunks.Any(static chunk => !chunk.IsComplete))
        {
            throw new HttpIOException(
                HttpRequestError.ResponseEnded,
                "The range queue ended before every segment completed.");
        }

        storage.SetLength(probe.TotalBytes);
        await storage.FlushAsync(cancellationToken).ConfigureAwait(false);
        await storage.DisposeAsync().ConfigureAwait(false);
        File.Move(partialFile, targetFile, overwrite: false);
        _progress(probe.TotalBytes, probe.TotalBytes);
        return new BuiltinRangeDownloadResult(receivedBytes, probe.TotalBytes);
    }

    private async Task<BuiltinRangeDownloadResult> DownloadSequentialAsync(
        string targetFile,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        var temporaryFile = $"{targetFile}.sequential.download";
        try
        {
            File.Delete(temporaryFile);
            using var request = CreateRequest(rangeStart: null, rangeEnd: null);
            using var response = await SendWithStallTimeoutAsync(request, cancellationToken)
                .ConfigureAwait(false);
            ThrowForHttpFailure(response);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new BuiltinRangeProtocolException(
                    "The media server did not return a complete sequential response.");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? expectedBytes;
            if (totalBytes <= 0)
            {
                throw new HttpIOException(
                    HttpRequestError.InvalidResponse,
                    "The media response did not declare its total length.");
            }

            if (expectedBytes > 0 && expectedBytes != totalBytes)
            {
                throw new BuiltinResumeRejectedException(
                    "The media length changed before the sequential transfer started.");
            }

            var directory = Path.GetDirectoryName(temporaryFile);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _planCreated?.Invoke(1);
            _progress(0, totalBytes);
            var source = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var sourceLifetime = source.ConfigureAwait(false);
            var storage = new FileStream(
                temporaryFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var storageLifetime = storage.ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long receivedBytes = 0;
            try
            {
                while (receivedBytes < totalBytes)
                {
                    var read = await ReadWithBandwidthLimitAsync(
                        source,
                        buffer.AsMemory(
                            0,
                            (int)Math.Min(buffer.Length, totalBytes - receivedBytes)),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new HttpIOException(
                            HttpRequestError.ResponseEnded,
                            "The sequential media response ended before the expected length.");
                    }

                    await storage.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                    receivedBytes += read;
                    _progress(receivedBytes, totalBytes);
                }

                if (await ReadWithBandwidthLimitAsync(
                        source,
                        buffer.AsMemory(0, 1),
                        cancellationToken).ConfigureAwait(false) != 0)
                {
                    throw new BuiltinResumeRejectedException(
                        "The sequential media response exceeded the expected length.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await storage.FlushAsync(cancellationToken).ConfigureAwait(false);
            await storage.DisposeAsync().ConfigureAwait(false);
            File.Move(temporaryFile, targetFile, overwrite: false);
            try
            {
                File.Delete($"{targetFile}.download");
            }
            catch (Exception cleanupException) when (cleanupException is IOException
                or UnauthorizedAccessException)
            {
                _logger.LogWarningMessage(
                    $"Built-in range checkpoint cleanup failed after the sequential " +
                    $"transfer committed; type={cleanupException.GetType().Name}.");
            }

            return new BuiltinRangeDownloadResult(receivedBytes, totalBytes);
        }
        catch
        {
            try
            {
                File.Delete(temporaryFile);
            }
            catch (Exception cleanupException) when (cleanupException is IOException
                or UnauthorizedAccessException)
            {
                _logger.LogWarningMessage(
                    $"Built-in sequential temporary cleanup failed while preserving the " +
                    $"primary transfer failure; type={cleanupException.GetType().Name}.");
            }

            throw;
        }
    }

    internal static IReadOnlyList<BuiltinRangeChunk> CreateChunks(
        long totalBytes,
        long segmentSize = DefaultSegmentSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentSize, 1);
        var chunks = new List<BuiltinRangeChunk>();
        for (var start = 0L; start < totalBytes; start = checked(start + segmentSize))
        {
            var end = Math.Min(totalBytes - 1, checked(start + segmentSize - 1));
            chunks.Add(new BuiltinRangeChunk(start, end, position: 0));
        }

        return chunks;
    }

    private async Task<BuiltinRangeProbe> ProbeAsync(
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(rangeStart: 0, rangeEnd: 0);
        using var response = await SendWithStallTimeoutAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ThrowForHttpFailure(response);

        long totalBytes;
        bool supportsRanges;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange is not { From: 0, To: 0, Length: > 0 }
                || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
            {
                throw new BuiltinRangeProtocolException(
                    "The range probe returned an invalid Content-Range header.");
            }

            totalBytes = contentRange.Length.Value;
            supportsRanges = true;
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            totalBytes = response.Content.Headers.ContentLength ?? expectedBytes;
            supportsRanges = false;
        }
        else
        {
            throw new BuiltinHttpStatusException(response.StatusCode, retryAfter: null);
        }

        if (totalBytes <= 0)
        {
            throw new HttpIOException(
                HttpRequestError.InvalidResponse,
                "The media response did not declare its total length.");
        }

        if (expectedBytes > 0 && expectedBytes != totalBytes)
        {
            throw new BuiltinResumeRejectedException(
                "The media length changed before the transfer started.");
        }

        return new BuiltinRangeProbe(
            totalBytes,
            supportsRanges,
            BuiltinRangeResourceIdentity.Create(_address, response));
    }

    private async Task<BuiltinRangeResumeState> LoadOrCreateStateAsync(
        string partialFile,
        long totalBytes,
        bool supportsRanges,
        BuiltinRangeResourceIdentity resourceIdentity,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(partialFile))
        {
            return await CreateFreshStateAsync(
                partialFile,
                totalBytes,
                supportsRanges,
                resourceIdentity,
                cancellationToken).ConfigureAwait(false);
        }

        if ((File.GetAttributes(partialFile) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The built-in partial file cannot be a redirected file.");
        }

        var stream = new FileStream(
            partialFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var streamLifetime = stream.ConfigureAwait(false);
        var metadataLength = stream.Length - totalBytes;
        if (metadataLength <= 0 || metadataLength > MaximumResumeMetadataBytes)
        {
            throw new BuiltinResumeRejectedException(
                "The built-in partial file has no valid resume metadata.");
        }

        var metadata = new byte[checked((int)metadataLength)];
        stream.Position = totalBytes;
        await stream.ReadExactlyAsync(metadata, cancellationToken).ConfigureAwait(false);
        BuiltinRangeResumeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<BuiltinRangeResumeDocument>(
                metadata,
                ResumeJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new BuiltinResumeRejectedException(
                "The built-in resume metadata is malformed.",
                exception);
        }

        if (document == null
            || document.TotalFileSize != totalBytes
            || !TryValidateChunks(document.Chunks, totalBytes, out var chunks))
        {
            throw new BuiltinResumeRejectedException(
                "The built-in resume metadata does not match the media file.");
        }

        var hasResumedBytes = chunks.Any(static chunk => chunk.Position > 0);
        if (!supportsRanges && (hasResumedBytes || chunks.Length != 1))
        {
            throw new BuiltinResumeRejectedException(
                "The server no longer supports resuming the partial media file.");
        }

        if (hasResumedBytes)
        {
            var continuity = document.ResourceIdentity?.CompareContinuity(resourceIdentity)
                ?? BuiltinRangeContinuity.Unavailable;
            switch (continuity)
            {
                case BuiltinRangeContinuity.Match:
                    break;
                case BuiltinRangeContinuity.Mismatch:
                    throw new BuiltinResumeRejectedException(
                        "The built-in resume metadata does not match the media file.");
                case BuiltinRangeContinuity.Unavailable:
                    resourceIdentity = await VerifyRetainedRangesAsync(
                        stream,
                        chunks,
                        totalBytes,
                        document.ResourceIdentity,
                        resourceIdentity,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new UnreachableException();
            }
        }

        return new BuiltinRangeResumeState(
            totalBytes,
            chunks,
            resourceIdentity);
    }

    private async Task<BuiltinRangeResumeState> CreateFreshStateAsync(
        string partialFile,
        long totalBytes,
        bool supportsRanges,
        BuiltinRangeResourceIdentity resourceIdentity,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(partialFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var adaptiveSegmentSize = CalculateAdaptiveSegmentSize(
            totalBytes,
            _parallelCount,
            _segmentSize);
        var chunks = supportsRanges && resourceIdentity.HasContinuityValidator
            ? CreateChunks(totalBytes, adaptiveSegmentSize).ToArray()
            : [new BuiltinRangeChunk(0, totalBytes - 1, position: 0)];
        var stream = new FileStream(
            partialFile,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using (var streamLifetime = stream.ConfigureAwait(false))
        {
            stream.SetLength(totalBytes);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new BuiltinRangeResumeState(
            totalBytes,
            chunks,
            resourceIdentity);
    }

    private static long CalculateAdaptiveSegmentSize(
        long totalBytes,
        int parallelCount,
        long segmentSize) =>
        Math.Min(segmentSize, Math.Max(1, ((totalBytes - 1) / parallelCount) + 1));

    private async Task<BuiltinRangeResourceIdentity> VerifyRetainedRangesAsync(
        FileStream partialFile,
        BuiltinRangeChunk[] chunks,
        long totalBytes,
        BuiltinRangeResourceIdentity? persistedIdentity,
        BuiltinRangeResourceIdentity resourceIdentity,
        CancellationToken cancellationToken)
    {
        var verifiedIdentity = resourceIdentity;
        foreach (var resumeChunk in chunks
                     .Where(static chunk => chunk.Position > 0)
                     .OrderBy(static chunk => chunk.Start))
        {
            verifiedIdentity = await VerifyRetainedChunkAsync(
                partialFile,
                resumeChunk,
                totalBytes,
                persistedIdentity,
                verifiedIdentity,
                cancellationToken).ConfigureAwait(false);
        }

        return verifiedIdentity;
    }

    private async Task<BuiltinRangeResourceIdentity> VerifyRetainedChunkAsync(
        FileStream partialFile,
        BuiltinRangeChunk resumeChunk,
        long totalBytes,
        BuiltinRangeResourceIdentity? persistedIdentity,
        BuiltinRangeResourceIdentity resourceIdentity,
        CancellationToken cancellationToken)
    {
        var retainedStart = resumeChunk.Start;
        var retainedEnd = checked(retainedStart + resumeChunk.Position - 1);
        using var request = CreateRequest(retainedStart, retainedEnd);
        using var response = await SendWithStallTimeoutAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ValidateChunkResponse(
            response,
            retainedStart,
            retainedEnd,
            totalBytes,
            supportsRanges: true,
            resourceIdentity);
        var retainedIdentity = BuiltinRangeResourceIdentity.Create(_address, response);
        if (persistedIdentity?.CompareContinuity(retainedIdentity)
            == BuiltinRangeContinuity.Mismatch)
        {
            throw new BuiltinResumeRejectedException(
                "The persisted resource identity changed before retained-range verification.");
        }

        var source = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var localBytes = ArrayPool<byte>.Shared.Rent(BufferSize);
        var remoteBytes = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var remaining = resumeChunk.Position;
            var fileOffset = retainedStart;
            while (remaining > 0)
            {
                var blockLength = checked((int)Math.Min(BufferSize, remaining));
                var localOffset = 0;
                while (localOffset < blockLength)
                {
                    var read = await RandomAccess.ReadAsync(
                        partialFile.SafeFileHandle,
                        localBytes.AsMemory(localOffset, blockLength - localOffset),
                        checked(fileOffset + localOffset),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "The local partial media ended before verification completed.");
                    }

                    localOffset += read;
                }

                var remoteOffset = 0;
                while (remoteOffset < blockLength)
                {
                    var read = await ReadWithBandwidthLimitAsync(
                        source,
                        remoteBytes.AsMemory(remoteOffset, blockLength - remoteOffset),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new HttpIOException(
                            HttpRequestError.ResponseEnded,
                            "The media retained-range response ended before verification completed.");
                    }

                    remoteOffset += read;
                }

                if (!localBytes.AsSpan(0, blockLength)
                        .SequenceEqual(remoteBytes.AsSpan(0, blockLength)))
                {
                    throw new BuiltinResumeRejectedException(
                        "The persisted partial media does not match the current resource.");
                }

                fileOffset = checked(fileOffset + blockLength);
                remaining -= blockLength;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(localBytes);
            ArrayPool<byte>.Shared.Return(remoteBytes);
        }

        return resourceIdentity with
        {
            StrongETag = retainedIdentity.StrongETag ?? resourceIdentity.StrongETag,
            LastModified = retainedIdentity.LastModified ?? resourceIdentity.LastModified
        };
    }

    private async Task<long> DownloadPendingChunksAsync(
        FileStream storage,
        BuiltinRangeResumeState state,
        bool supportsRanges,
        long initialReceivedBytes,
        CancellationToken cancellationToken)
    {
        var queue = new ConcurrentQueue<BuiltinRangeChunk>(
            state.Chunks.Where(static chunk => !chunk.IsComplete));
        if (queue.IsEmpty)
        {
            return initialReceivedBytes;
        }

        using var workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        ExceptionDispatchInfo? firstFailure = null;
        var receivedBytes = initialReceivedBytes;
        var workerCount = Math.Min(_parallelCount, queue.Count);
        var workers = Enumerable.Range(0, workerCount)
            .Select(async _ =>
            {
                while (queue.TryDequeue(out var chunk))
                {
                    try
                    {
                        await DownloadChunkAsync(
                            storage,
                            state,
                            chunk,
                            supportsRanges,
                            bytesReceived =>
                            {
                                var total = Interlocked.Add(ref receivedBytes, bytesReceived);
                                _progress(total, state.TotalBytes);
                            },
                            workersCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        workersCancellation.IsCancellationRequested && firstFailure != null)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(
                            ref firstFailure,
                            ExceptionDispatchInfo.Capture(exception),
                            null);
                        await workersCancellation.CancelAsync().ConfigureAwait(false);
                        throw;
                    }
                }
            })
            .ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            firstFailure?.Throw();
            throw;
        }

        return receivedBytes;
    }

    private async Task DownloadChunkAsync(
        FileStream storage,
        BuiltinRangeResumeState state,
        BuiltinRangeChunk chunk,
        bool supportsRanges,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        // A single unvalidated representation cannot safely race independent ranges.
        // Bound speculative memory and duplicate traffic to one range per transfer.
        if (!supportsRanges || !state.ResourceIdentity.HasContinuityValidator ||
            chunk.End - chunk.Start + 1 > DefaultSegmentSize)
        {
            await DownloadChunkBodyAsync(storage, state, chunk, supportsRanges,
                reportBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waiting = 0;
        var initialPosition = chunk.Position;
        var started = Stopwatch.GetTimestamp();
        var primary = DownloadChunkBodyAsync(storage, state, chunk, supportsRanges,
            reportBytes, lifetime.Token, value => Interlocked.Exchange(ref waiting, value ? 1 : 0));
        try
        {
            using var timer = new PeriodicTimer(_hedgeDelay);
            while (!primary.IsCompleted)
            {
                if (await Task.WhenAny(primary, timer.WaitForNextTickAsync(cancellationToken).AsTask())
                        .ConfigureAwait(false) == primary)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var rate = (chunk.Position - initialPosition) / Stopwatch.GetElapsedTime(started).TotalSeconds;
                var slowThreshold = Math.Max(16 * 1024, Interlocked.Read(ref _referenceBytesPerSecond) / 4);
                if (Volatile.Read(ref waiting) == 0 || rate >= slowThreshold ||
                    _bandwidthLimiter?.IsLimited == true ||
                    !await _hedgeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    await RaceChunkAsync(storage, state, chunk, primary, lifetime,
                        reportBytes, cancellationToken).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    _hedgeGate.Release();
                }
            }

            await primary.ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            // Also join on monitor/caller failure; no old writer survives this lease.
            await ObserveRevokedWorkerAsync(primary, lifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task RaceChunkAsync(
        FileStream storage, BuiltinRangeResumeState state, BuiltinRangeChunk chunk,
        Task primary, CancellationTokenSource primaryLifetime, Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        using var hedgeLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var candidateStart = checked(chunk.Start + chunk.Position);
        if (candidateStart > chunk.End)
        {
            await primary.ConfigureAwait(false);
            return;
        }

        _logger.LogInformationMessage("Slow range suspected; speculativeAttempt=started; maximumHedges=1.");
        var candidate = ReadChunkCandidateAsync(state, candidateStart, chunk.End, hedgeLifetime.Token);
        try
        {
            var winner = await Task.WhenAny(primary, candidate).ConfigureAwait(false);
            if (winner == primary)
            {
                try
                {
                    await primary.ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException or HttpIOException or TimeoutException)
                {
                    // Only a successful, validated completion wins. A transport
                    // failure must not cancel a still viable backup range.
                    _logger.LogDebugMessage($"Primary range failed during speculation; failure={exception.GetType().Name}.");
                }
            }

            byte[] bytes;
            try
            {
                bytes = await candidate.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException)
            {
                if (exception is BuiltinResumeRejectedException)
                {
                    throw;
                }

                // A speculative transport failure does not revoke a usable primary.
                _logger.LogDebugMessage($"Speculative range failed; failure={exception.GetType().Name}.");
                await primary.ConfigureAwait(false);
                return;
            }
            await primaryLifetime.CancelAsync().ConfigureAwait(false);
            await ObserveRevokedWorkerAsync(primary, primaryLifetime.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // There is now exactly one writer. Count only newly committed bytes,
            // never the duplicated network traffic from the speculative request.
            await RandomAccess.WriteAsync(storage.SafeFileHandle, bytes, candidateStart,
                cancellationToken).ConfigureAwait(false);
            var committed = checked((int)(chunk.End - chunk.Start + 1 - chunk.Position));
            chunk.Advance(committed);
            reportBytes(committed);
            _logger.LogInformationMessage("Speculative range committed; predecessor=drained.");
        }
        finally
        {
            await hedgeLifetime.CancelAsync().ConfigureAwait(false);
            await ObserveRevokedWorkerAsync(candidate, hedgeLifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> ReadChunkCandidateAsync(
        BuiltinRangeResumeState state, long start, long end, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(start, end, state.ResourceIdentity.IfRangeValue);
        using var response = await SendWithStallTimeoutAsync(request, cancellationToken).ConfigureAwait(false);
        ValidateChunkResponse(response, start, end, state.TotalBytes, true, state.ResourceIdentity);
        if (state.ResourceIdentity.CompareContinuity(BuiltinRangeResourceIdentity.Create(_address, response))
            != BuiltinRangeContinuity.Match)
        {
            throw new BuiltinResumeRejectedException("A speculative range must confirm the same resource version.");
        }
        var bytes = new byte[checked((int)(end - start + 1))];
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var streamLifetime = stream.ConfigureAwait(false);
        var position = 0;
        while (position < bytes.Length)
        {
            var read = await ReadWithBandwidthLimitAsync(stream, bytes.AsMemory(position), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The speculative range ended early.");
            }

            position += read;
        }

        return bytes;
    }

    private async Task ObserveRevokedWorkerAsync(Task worker, CancellationToken revoked)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (revoked.IsCancellationRequested)
        {
            // Acknowledgement of the lease revocation, not a transfer failure.
            _logger.LogDebugMessage("Revoked range worker acknowledged cancellation.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException)
        {
            // Keep resource mismatches fatal even when the other attempt won.
            if (exception is BuiltinResumeRejectedException)
            {
                throw;
            }

            _logger.LogDebugMessage($"Revoked range joined; failure={exception.GetType().Name}.");
        }
    }

    private async Task DownloadChunkBodyAsync(
        FileStream storage,
        BuiltinRangeResumeState state,
        BuiltinRangeChunk chunk,
        bool supportsRanges,
        Action<int> reportBytes,
        CancellationToken cancellationToken,
        Action<bool>? readActivity = null)
    {
        var started = Stopwatch.GetTimestamp();
        var requestStart = checked(chunk.Start + chunk.Position);
        using var request = supportsRanges
            ? CreateRequest(
                requestStart,
                chunk.End,
                state.ResourceIdentity.IfRangeValue)
            : CreateRequest(rangeStart: null, rangeEnd: null);
        using var response = await SendWithStallTimeoutAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ValidateChunkResponse(
            response,
            requestStart,
            chunk.End,
            state.TotalBytes,
            supportsRanges,
            state.ResourceIdentity);

        var remaining = checked(chunk.End - requestStart + 1);
        var source = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (remaining > 0)
            {
                int read;
                readActivity?.Invoke(true);
                try
                {
                    read = await ReadWithBandwidthLimitAsync(source,
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    readActivity?.Invoke(false);
                }
                if (read == 0)
                {
                    throw new HttpIOException(
                        HttpRequestError.ResponseEnded,
                        "The media response ended before the requested range completed.");
                }

                var fileOffset = checked(chunk.Start + chunk.Position);
                await RandomAccess.WriteAsync(
                    storage.SafeFileHandle,
                    buffer.AsMemory(0, read),
                    fileOffset,
                    cancellationToken).ConfigureAwait(false);
                chunk.Advance(read);
                remaining -= read;
                reportBytes(read);
            }

            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var rate = checked((long)Math.Min((chunk.End - requestStart + 1) / Math.Max(elapsed, 0.001), long.MaxValue));
            var previous = Interlocked.Read(ref _referenceBytesPerSecond);
            while (rate > previous)
            {
                var observed = Interlocked.CompareExchange(ref _referenceBytesPerSecond, rate, previous);
                if (observed == previous)
                {
                    break;
                }

                previous = observed;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private BuiltinRangeDownloadResult? TryRecoverCompletedTarget(
        string targetFile,
        long expectedBytes)
    {
        if (!TryGetCompletedTarget(targetFile, expectedBytes, out var result))
        {
            return null;
        }

        _progress(result.ReceivedBytes, result.TotalBytes);
        return result;
    }

    internal static bool TryGetCompletedTarget(
        string targetFile,
        long expectedBytes,
        out BuiltinRangeDownloadResult result)
    {
        result = null!;
        if (expectedBytes <= 0 || !File.Exists(targetFile))
        {
            return false;
        }

        if ((File.GetAttributes(targetFile) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The completed built-in download cannot be a redirected file.");
        }

        var actualBytes = new FileInfo(targetFile).Length;
        if (actualBytes != expectedBytes)
        {
            throw new BuiltinResumeRejectedException(
                "The completed built-in download does not match the expected length.");
        }

        var integrity = DownloadFileIntegrity.Check(targetFile, expectedBytes);
        if (!integrity.IsUsable)
        {
            throw new BuiltinResumeRejectedException(
                integrity.Reason ?? "The completed built-in download is not usable.");
        }

        result = new BuiltinRangeDownloadResult(actualBytes, expectedBytes);
        return true;
    }

    private async Task<HttpResponseMessage> SendWithStallTimeoutAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var stallCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        stallCancellation.CancelAfter(_readStallTimeout);
        _networkReadStateChanged?.Invoke(true);
        try
        {
            return await _http.SendAsync(request, stallCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && stallCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The media server stopped before sending response headers.",
                exception);
        }
        finally
        {
            _networkReadStateChanged?.Invoke(false);
        }
    }

    private async ValueTask<int> ReadWithStallTimeoutAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        using var stallCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        stallCancellation.CancelAfter(_readStallTimeout);
        _networkReadStateChanged?.Invoke(true);
        try
        {
            var read = await source.ReadAsync(buffer, stallCancellation.Token).ConfigureAwait(false);
            _networkBytesReceived?.Invoke(read);
            return read;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The media response stopped producing data.",
                exception);
        }
        finally
        {
            _networkReadStateChanged?.Invoke(false);
        }
    }

    private async ValueTask<int> ReadWithBandwidthLimitAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (_bandwidthLimiter == null)
        {
            return await ReadWithStallTimeoutAsync(source, buffer, cancellationToken)
                .ConfigureAwait(false);
        }

        var permit = await _bandwidthLimiter
            .AcquireAsync(buffer.Length, cancellationToken)
            .ConfigureAwait(false);
        var unusedBytes = permit.ByteCount;
        try
        {
            var read = await ReadWithStallTimeoutAsync(
                source,
                buffer[..permit.ByteCount],
                cancellationToken).ConfigureAwait(false);
            unusedBytes -= read;
            return read;
        }
        finally
        {
            _bandwidthLimiter.ReturnUnused(permit, unusedBytes);
        }
    }

    private async Task<long> DownloadWithCheckpointsAsync(
        FileStream storage,
        BuiltinRangeResumeState state,
        bool supportsRanges,
        long initialReceivedBytes,
        CancellationToken cancellationToken)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workers = DownloadPendingChunksAsync(storage, state, supportsRanges,
            initialReceivedBytes, lifetime.Token);
        var checkpoints = CheckpointLoopAsync(storage, state, workers, lifetime);
        try
        {
            return await workers.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await checkpoints.ConfigureAwait(false);
            }
            finally
            {
                lifetime.Dispose();
            }
        }
    }

    private async Task CheckpointLoopAsync(
        FileStream storage, BuiltinRangeResumeState state, Task workers, CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (!workers.IsCompleted)
            {
                if (await Task.WhenAny(workers, timer.WaitForNextTickAsync(lifetime.Token).AsTask())
                        .ConfigureAwait(false) == workers)
                {
                    break;
                }

                lifetime.Token.ThrowIfCancellationRequested();
                await SaveCheckpointAsync(storage, state, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed durable checkpoint terminates its workers; it cannot leave
            // an unobserved background failure or outlive the shared file handle.
            await lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task SaveCheckpointAsync(
        FileStream storage,
        BuiltinRangeResumeState state,
        CancellationToken cancellationToken)
    {
        await _checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new BuiltinRangeResumeDocument(
                state.TotalBytes,
                state.Chunks.Select(static chunk => new BuiltinRangeResumeChunk(
                    chunk.Start,
                    chunk.End,
                    chunk.Position)).ToArray(),
                state.ResourceIdentity);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(document, ResumeJsonOptions);
            if (metadata.Length > MaximumResumeMetadataBytes)
            {
                throw new IOException("The built-in resume metadata exceeded its safety limit.");
            }

            await storage.FlushAsync(cancellationToken).ConfigureAwait(false);
            storage.SetLength(state.TotalBytes);
            await RandomAccess.WriteAsync(
                storage.SafeFileHandle,
                metadata,
                state.TotalBytes,
                cancellationToken).ConfigureAwait(false);
            storage.SetLength(checked(state.TotalBytes + metadata.Length));
            await storage.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    private HttpRequestMessage CreateRequest(
        long? rangeStart,
        long? rangeEnd,
        string? ifRange = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _address);
        if (rangeStart.HasValue)
        {
            request.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);
            if (!string.IsNullOrWhiteSpace(ifRange))
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange);
            }
        }

        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.TryAddWithoutValidation("User-Agent", _headers.UserAgent);
        foreach (var header in _headers.Headers)
        {
            var separator = header.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                request.Dispose();
                throw new InvalidOperationException("A built-in download header is malformed.");
            }

            request.Headers.TryAddWithoutValidation(
                header[..separator],
                header[(separator + 1)..].TrimStart());
        }

        return request;
    }

    private void ValidateChunkResponse(
        HttpResponseMessage response,
        long requestStart,
        long requestEnd,
        long totalBytes,
        bool supportsRanges,
        BuiltinRangeResourceIdentity resourceIdentity)
    {
        ThrowForHttpFailure(response);
        var expectedLength = checked(requestEnd - requestStart + 1);
        if (supportsRanges
            && response.StatusCode == HttpStatusCode.OK
            && !string.IsNullOrWhiteSpace(resourceIdentity.IfRangeValue))
        {
            var continuity = resourceIdentity.CompareContinuity(
                BuiltinRangeResourceIdentity.Create(_address, response));
            if (continuity != BuiltinRangeContinuity.Match)
            {
                throw new BuiltinResumeRejectedException(
                    "The media response resource identity changed during the transfer.");
            }

            throw new BuiltinRangeProtocolException(
                "The media server did not honor the requested byte range.");
        }

        if (supportsRanges)
        {
            var contentRange = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent
                || contentRange?.From != requestStart
                || contentRange.To != requestEnd
                || contentRange.Length != totalBytes
                || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
            {
                throw new BuiltinRangeProtocolException(
                    "The media server did not honor the requested byte range.");
            }
        }
        else if (response.StatusCode != HttpStatusCode.OK || requestStart != 0)
        {
            throw new BuiltinRangeProtocolException(
                "The media server cannot resume the partial response.");
        }

        if (response.Content.Headers.ContentLength is { } contentLength
            && contentLength != expectedLength)
        {
            throw new BuiltinRangeProtocolException(
                "The media response length did not match the requested byte range.");
        }

        if (supportsRanges
            && resourceIdentity.CompareContinuity(
                BuiltinRangeResourceIdentity.Create(_address, response))
            == BuiltinRangeContinuity.Mismatch)
        {
            throw new BuiltinResumeRejectedException(
                "The media response resource identity changed during the transfer.");
        }
    }

    private static void ThrowForHttpFailure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter == null && response.Headers.RetryAfter?.Date is { } retryDate)
        {
            retryAfter = retryDate - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero)
            {
                retryAfter = TimeSpan.Zero;
            }
        }

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new BuiltinRangeProtocolException(
                "The media server rejected the requested resume range.");
        }

        throw new BuiltinHttpStatusException(response.StatusCode, retryAfter);
    }

    private static bool TryValidateChunks(
        BuiltinRangeResumeChunk[]? serializedChunks,
        long totalBytes,
        out BuiltinRangeChunk[] chunks)
    {
        chunks = [];
        if (serializedChunks is not { Length: > 0 })
        {
            return false;
        }

        var validated = new BuiltinRangeChunk[serializedChunks.Length];
        var expectedStart = 0L;
        for (var index = 0; index < serializedChunks.Length; index++)
        {
            var serialized = serializedChunks[index];
            if (serialized.Start != expectedStart
                || serialized.End < serialized.Start
                || serialized.End >= totalBytes)
            {
                return false;
            }

            var length = checked(serialized.End - serialized.Start + 1);
            if (serialized.Position < 0 || serialized.Position > length)
            {
                return false;
            }

            validated[index] = new BuiltinRangeChunk(
                serialized.Start,
                serialized.End,
                serialized.Position);
            expectedStart = checked(serialized.End + 1);
        }

        if (expectedStart != totalBytes)
        {
            return false;
        }

        chunks = validated;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _checkpointGate.Dispose();
        _hedgeGate.Dispose();
    }
}

internal sealed class BuiltinRangeChunk(long start, long end, long position)
{
    private long _position = position;

    public long Start { get; } = start;

    public long End { get; } = end;

    public long Position => Interlocked.Read(ref _position);

    public bool IsComplete => Position >= checked(End - Start + 1);

    public void Advance(int count) => Interlocked.Add(ref _position, count);
}

internal sealed record BuiltinRangeDownloadResult(long ReceivedBytes, long TotalBytes);

internal sealed record BuiltinRangeProbe(
    long TotalBytes,
    bool SupportsRanges,
    BuiltinRangeResourceIdentity ResourceIdentity);

internal sealed record BuiltinRangeResumeState(
    long TotalBytes,
    BuiltinRangeChunk[] Chunks,
    BuiltinRangeResourceIdentity ResourceIdentity);

internal sealed record BuiltinRangeResumeDocument(
    long TotalFileSize,
    BuiltinRangeResumeChunk[] Chunks,
    BuiltinRangeResourceIdentity? ResourceIdentity = null);

internal sealed record BuiltinRangeResumeChunk(long Start, long End, long Position);

internal sealed record BuiltinRangeResourceIdentity(
    string? AddressHash,
    string? StrongETag,
    DateTimeOffset? LastModified)
{
    private static readonly TimeSpan MinimumStrongLastModifiedAge =
        TimeSpan.FromSeconds(60);

    public string? IfRangeValue => StrongETag ?? LastModified?.ToString("R");

    public bool HasContinuityValidator => !string.IsNullOrWhiteSpace(IfRangeValue);

    public static BuiltinRangeResourceIdentity Create(
        Uri address,
        HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(response);
        var entityTag = response.Headers.ETag is { IsWeak: false } strongEntityTag
            ? strongEntityTag.ToString()
            : null;
        var lastModified = response.Content.Headers.LastModified is { } modified
                           && response.Headers.Date is { } responseDate
                           && responseDate - modified >= MinimumStrongLastModifiedAge
            ? (DateTimeOffset?)modified
            : null;
        return new BuiltinRangeResourceIdentity(
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(address.AbsoluteUri))),
            entityTag,
            lastModified);
    }

    public BuiltinRangeContinuity CompareContinuity(
        BuiltinRangeResourceIdentity current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (AddressHash == null
            || current.AddressHash == null
            || !string.Equals(AddressHash, current.AddressHash, StringComparison.Ordinal))
        {
            return BuiltinRangeContinuity.Unavailable;
        }

        if (StrongETag != null && current.StrongETag != null)
        {
            return string.Equals(StrongETag, current.StrongETag, StringComparison.Ordinal)
                ? BuiltinRangeContinuity.Match
                : BuiltinRangeContinuity.Mismatch;
        }

        if (LastModified != null && current.LastModified != null)
        {
            return LastModified == current.LastModified
                ? BuiltinRangeContinuity.Match
                : BuiltinRangeContinuity.Mismatch;
        }

        return BuiltinRangeContinuity.Unavailable;
    }
}

internal enum BuiltinRangeContinuity
{
    Unavailable,
    Match,
    Mismatch
}

internal class BuiltinResumeRejectedException : IOException
{
    public BuiltinResumeRejectedException()
    {
    }

    public BuiltinResumeRejectedException(string message)
        : base(message)
    {
    }

    public BuiltinResumeRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

}

internal sealed class BuiltinRangeProtocolException : BuiltinResumeRejectedException
{
    public BuiltinRangeProtocolException()
    {
    }

    public BuiltinRangeProtocolException(string message)
        : base(message)
    {
    }

    public BuiltinRangeProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class BuiltinHttpStatusException : HttpRequestException
{
    public BuiltinHttpStatusException()
    {
    }

    public BuiltinHttpStatusException(string message)
        : base(message)
    {
    }

    public BuiltinHttpStatusException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BuiltinHttpStatusException(HttpStatusCode statusCode, TimeSpan? retryAfter)
        : base("The media server rejected the built-in download request.", null, statusCode)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}
