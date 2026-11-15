using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Download;

/// <summary>
/// Owns the existing JSON trailer in the partial file. Callers supply one writer
/// and await it before closing or publishing the file.
/// </summary>
internal static class BuiltinRangeCheckpoint
{
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<(BuiltinRangeChunk[] Chunks, BuiltinRangeResourceIdentity? Identity)> ReadAsync(
        FileStream storage, long totalBytes, CancellationToken cancellationToken)
    {
        var metadataLength = storage.Length - totalBytes;
        if (metadataLength <= 0 || metadataLength > MaximumMetadataBytes)
        {
            throw new BuiltinResumeRejectedException(
                "The built-in partial file has no valid resume metadata.");
        }

        var metadata = new byte[checked((int)metadataLength)];
        storage.Position = totalBytes;
        await storage.ReadExactlyAsync(metadata, cancellationToken).ConfigureAwait(false);
        BuiltinRangeResumeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<BuiltinRangeResumeDocument>(metadata, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new BuiltinResumeRejectedException("The built-in resume metadata is malformed.", exception);
        }

        if (document == null || document.TotalFileSize != totalBytes ||
            !TryValidateChunks(document.Chunks, totalBytes, out var chunks))
        {
            throw new BuiltinResumeRejectedException(
                "The built-in resume metadata does not match the media file.");
        }

        return (chunks, document.ResourceIdentity);
    }

    public static async Task WriteAsync(
        FileStream storage, BuiltinRangeResumeState state, CancellationToken cancellationToken)
    {
        var document = new BuiltinRangeResumeDocument(
            state.TotalBytes,
            state.Chunks.Select(static chunk => new BuiltinRangeResumeChunk(
                chunk.Start, chunk.End, chunk.Position)).ToArray(),
            state.ResourceIdentity);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (metadata.Length > MaximumMetadataBytes)
        {
            throw new IOException("The built-in resume metadata exceeded its safety limit.");
        }

        await storage.FlushAsync(cancellationToken).ConfigureAwait(false);
        storage.SetLength(state.TotalBytes);
        await RandomAccess.WriteAsync(
            storage.SafeFileHandle, metadata, state.TotalBytes, cancellationToken).ConfigureAwait(false);
        storage.SetLength(checked(state.TotalBytes + metadata.Length));
        await storage.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryValidateChunks(
        BuiltinRangeResumeChunk?[]? serializedChunks,
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
            if (serialized == null
                || serialized.Start != expectedStart
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

}

internal sealed record BuiltinRangeResumeDocument(
    long TotalFileSize,
    BuiltinRangeResumeChunk[] Chunks,
    BuiltinRangeResourceIdentity? ResourceIdentity = null);

internal sealed record BuiltinRangeResumeChunk(long Start, long End, long Position);
