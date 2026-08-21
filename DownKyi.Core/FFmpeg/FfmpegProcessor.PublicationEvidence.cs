using DownKyi.Application.Downloads;

namespace DownKyi.Core.FFmpeg;

public sealed partial class FfmpegProcessor
{
    private async Task<bool> RunToFileSucceededAsync(
        Func<string, FfmpegCommand> commandFactory,
        string destination,
        bool overwriteDestination,
        Action<string>? action,
        CancellationToken cancellationToken)
    {
        var result = await RunToFileAsync(
            commandFactory,
            destination,
            overwriteDestination,
            action: action,
            outputArtifactOwnershipProvider: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    private static async Task<bool> VerifyPublishedIdentityAsync(
        IOutputArtifactOwnershipProvider? outputArtifactOwnershipProvider,
        string destination,
        OutputArtifactPublicationEvidence publicationEvidence)
    {
        if (outputArtifactOwnershipProvider is null)
        {
            return false;
        }

        try
        {
            // Once the move succeeded, absence of verification deliberately
            // produces an untracked output instead of changing publish success.
            return await outputArtifactOwnershipProvider
                .VerifyPublishedObjectIdentityAsync(
                    destination,
                    publicationEvidence,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                         or UnauthorizedAccessException
                                         or InvalidOperationException
                                         or ArgumentException
                                         or NotSupportedException)
        {
            return false;
        }
    }
}
