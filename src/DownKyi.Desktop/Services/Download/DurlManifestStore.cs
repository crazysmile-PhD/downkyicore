using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

// The manifest is a task-owned artifact. It records the complete segment set without
// persisting expiring playback addresses, so completed video can survive an audio retry.
internal static class DurlManifestStore
{
    private const string TransferKey = "durl-manifest";

    public static async Task<bool> EnsureRecordedAsync(
        DownloadExecutionContext context,
        IReadOnlyList<PlayUrlDurl> segments,
        DownloadTaskProjectionStore projectionStore,
        DownloadTaskStateWriter stateWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(segments);
        var directory = context.DownloadDirectory
            ?? throw new InvalidOperationException("Download directory is unavailable.");
        var expected = CreateManifest(context, segments);
        var snapshot = projectionStore.GetRequiredSnapshot(context.TaskId);
        var recorded = snapshot.Plan.TransferFiles.TryGetValue(TransferKey, out var fileName);
        if (recorded && !IsSafeFileName(fileName))
        {
            return false;
        }

        fileName ??= $"{Guid.NewGuid():N}.durl-manifest.json";
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path))
        {
            var existing = TryRead(path);
            if (existing == null || !SameShape(existing, expected))
            {
                return false;
            }
        }
        else
        {
            if (!recorded)
            {
                await stateWriter.ClaimTransferFileAsync(
                    context.TaskId, TransferKey, fileName, cancellationToken).ConfigureAwait(false);
            }

            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporary, JsonSerializer.Serialize(expected), cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporary, path, overwrite: false);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        if (!snapshot.Transfer.CompletedFileKeys.Contains(TransferKey, StringComparer.Ordinal))
        {
            await stateWriter.CompleteTransferFileAsync(
                context.TaskId, TransferKey, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public static bool TryRestoreCompleted(DownloadExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.Input;
        if (input.RequestedContent.MediaKind is not
                (DownloadMediaKind.Durl or DownloadMediaKind.DurlWithDashAudio)
            || context.DownloadDirectory == null
            || !input.CompletedTransferKeys.Contains(TransferKey, StringComparer.Ordinal)
            || !input.TransferFiles.TryGetValue(TransferKey, out var fileName)
            || !IsSafeFileName(fileName))
        {
            return false;
        }

        var manifest = TryRead(Path.Combine(context.DownloadDirectory, fileName));
        if (manifest == null
            || manifest.Quality != input.Metadata.Resolution.Id
            || !string.Equals(
                manifest.CodecName, input.Metadata.VideoCodecName, StringComparison.Ordinal))
        {
            return false;
        }

        var restored = manifest.Segments.Select(segment =>
        {
            var key = DownloadTransferKey.Create(segment.Order, "durl");
            if (!input.CompletedTransferKeys.Contains(key, StringComparer.Ordinal)
                || !input.TransferFiles.TryGetValue(key, out var segmentFileName)
                || !IsSafeFileName(segmentFileName))
            {
                return null;
            }

            var path = Path.Combine(context.DownloadDirectory, segmentFileName);
            return DownloadFileIntegrity.Check(path, segment.Size).IsUsable
                ? new DurlDownloadResult(
                    new PlayUrlDurl
                    {
                        Order = segment.Order,
                        Length = segment.Length,
                        Size = segment.Size,
                        Source = segment.Source
                    },
                    path,
                    key)
                : null;
        }).ToArray();
        if (restored.Any(segment => segment == null))
        {
            return false;
        }

        context.DurlDownloads = restored.Select(segment => segment!).ToArray();
        return true;
    }

    private static DurlManifest CreateManifest(
        DownloadExecutionContext context,
        IReadOnlyList<PlayUrlDurl> segments) => new(
        context.Input.Metadata.Resolution.Id,
        context.Input.Metadata.VideoCodecName,
        segments.Select(segment => new DurlSegment(
            segment.Order, segment.Length, segment.Size, segment.Source)).ToArray());

    private static bool SameShape(DurlManifest left, DurlManifest right) =>
        left.Quality == right.Quality
        && string.Equals(left.CodecName, right.CodecName, StringComparison.Ordinal)
        && left.Segments.Length == right.Segments.Length
        && left.Segments.Zip(right.Segments)
            .All(pair => pair.First.Order == pair.Second.Order
                         && pair.First.Length == pair.Second.Length
                         && pair.First.Size == pair.Second.Size);

    private static DurlManifest? TryRead(string path)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<DurlManifest>(File.ReadAllText(path));
            if (manifest is not { Quality: > 0, CodecName.Length: > 0, Segments.Length: > 0 }
                || manifest.Segments.Any(segment => !IsValidSegment(segment))
                || manifest.Segments.Select(segment => segment.Order).Distinct().Count()
                != manifest.Segments.Length)
            {
                return null;
            }

            return manifest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                         or JsonException)
        {
            return null;
        }
    }

    private static bool IsSafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName is not ("." or "..")
        && !Path.IsPathRooted(fileName)
        && fileName == Path.GetFileName(fileName);

    private static bool IsValidSegment(DurlSegment? segment) =>
        segment is { Order: > 0, Length: >= 0, Size: >= 0 }
        && (segment.Source == null || Enum.IsDefined(segment.Source.Value));

    private sealed record DurlManifest(int Quality, string CodecName, DurlSegment[] Segments);

    private sealed record DurlSegment(
        int Order,
        long Length,
        long Size,
        PlayUrlResolutionSource? Source);
}
