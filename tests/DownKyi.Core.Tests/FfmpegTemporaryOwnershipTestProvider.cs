using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Core.Tests;

internal sealed class FfmpegTemporaryOwnershipTestProvider : IOutputArtifactOwnershipProvider
{
    private sealed record TemporaryClaim : OutputArtifactTemporaryClaim;

    public static FfmpegTemporaryOwnershipTestProvider Instance { get; } = new();

    public OutputArtifactTemporaryClaimResult ClaimTemporaryObject(
        SafeFileHandle temporaryHandle)
    {
        Assert.False(temporaryHandle.IsClosed);
        Assert.False(temporaryHandle.IsInvalid);
        return OutputArtifactTemporaryClaimResult.Claimed(new TemporaryClaim());
    }

    public Task<OutputArtifactSafeDeleteResult> DeleteTemporaryIfOwnedAsync(
        string temporaryPath,
        OutputArtifactTemporaryClaim temporaryClaim,
        CancellationToken cancellationToken)
    {
        Assert.IsType<TemporaryClaim>(temporaryClaim);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(temporaryPath))
        {
            return Task.FromResult(OutputArtifactSafeDeleteResult.Missing());
        }

        File.Delete(temporaryPath);
        return Task.FromResult(OutputArtifactSafeDeleteResult.DeletedResult());
    }

    public Task<OutputArtifactSafeDeleteResult> DeleteIfOwnedAsync(
        string candidatePath,
        DownloadOutputArtifactProvenance provenance,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(OutputArtifactSafeDeleteResult.Unsupported());
    }
}
