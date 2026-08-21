using DownKyi.Application.Downloads;

namespace DownKyi.Core.FFmpeg;

internal sealed record FfmpegTemporaryOutput(
    string Path,
    OutputArtifactTemporaryClaim? Claim)
{
    public static FfmpegTemporaryOutput Create(
        string path,
        IOutputArtifactOwnershipProvider? ownershipProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        if (ownershipProvider is null)
        {
            return new FfmpegTemporaryOutput(path, null);
        }

        try
        {
            var claim = ownershipProvider.ClaimTemporaryObject(stream.SafeFileHandle);
            return new FfmpegTemporaryOutput(
                path,
                claim.Succeeded ? claim.Claim : null);
        }
        catch (Exception exception) when (exception is IOException
                                         or UnauthorizedAccessException
                                         or InvalidOperationException
                                         or ArgumentException
                                         or NotSupportedException)
        {
            return new FfmpegTemporaryOutput(path, null);
        }
    }

    public async Task DeleteIfOwnedAsync(
        IOutputArtifactOwnershipProvider? ownershipProvider)
    {
        if (ownershipProvider is null || Claim is null)
        {
            return;
        }

        try
        {
            await ownershipProvider
                .DeleteTemporaryIfOwnedAsync(Path, Claim, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                         or UnauthorizedAccessException
                                         or InvalidOperationException
                                         or ArgumentException
                                         or NotSupportedException)
        {
            return;
        }
    }
}
