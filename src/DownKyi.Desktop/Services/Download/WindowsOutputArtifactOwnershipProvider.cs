using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Services.Download;

/// <summary>
/// Windows implementation of final-output ownership validation. It opens the
/// candidate once, reads its identity and content through that handle, and
/// marks that same handle for deletion only after validation succeeds.
/// </summary>
internal sealed class WindowsOutputArtifactOwnershipProvider
    : IOutputArtifactOwnershipProvider
{
    internal const string IdentityProviderName = "windows-file-id-v1";

    private readonly IOutputArtifactNativeFileSystem _fileSystem;

    public WindowsOutputArtifactOwnershipProvider()
        : this(new WindowsOutputArtifactNativeFileSystem())
    {
    }

    internal WindowsOutputArtifactOwnershipProvider(
        IOutputArtifactNativeFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    public OutputArtifactTemporaryClaimResult ClaimTemporaryObject(
        SafeFileHandle temporaryHandle)
    {
        ArgumentNullException.ThrowIfNull(temporaryHandle);
        if (!_fileSystem.IsSupported)
        {
            return OutputArtifactTemporaryClaimResult.Unsupported();
        }

        var capture = _fileSystem.CaptureIdentity(temporaryHandle);
        return capture.Status switch
        {
            OutputArtifactNativeIdentityCaptureStatus.Captured
                when !string.IsNullOrWhiteSpace(capture.FilesystemIdentity) =>
                OutputArtifactTemporaryClaimResult.Claimed(
                    new WindowsOutputArtifactTemporaryClaim(capture.FilesystemIdentity)),
            OutputArtifactNativeIdentityCaptureStatus.Unsupported =>
                OutputArtifactTemporaryClaimResult.Unsupported(),
            _ => OutputArtifactTemporaryClaimResult.Failed()
        };
    }

    public async Task<OutputArtifactEvidenceCaptureResult> CapturePublicationEvidenceAsync(
        string temporaryPath,
        OutputArtifactTemporaryClaim temporaryClaim,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(temporaryClaim);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_fileSystem.IsSupported)
        {
            return OutputArtifactEvidenceCaptureResult.Unsupported();
        }

        if (temporaryClaim is not WindowsOutputArtifactTemporaryClaim windowsClaim)
        {
            return OutputArtifactEvidenceCaptureResult.Failed();
        }

        var capture = await _fileSystem
            .CaptureEvidenceAsync(temporaryPath, cancellationToken)
            .ConfigureAwait(false);
        return capture.Status switch
        {
            OutputArtifactNativeCaptureStatus.Captured
                when capture.Evidence is not null
                     && string.Equals(
                         capture.Evidence.FilesystemIdentity,
                         windowsClaim.FilesystemIdentity,
                         StringComparison.Ordinal) =>
                OutputArtifactEvidenceCaptureResult.Captured(
                    new OutputArtifactPublicationEvidence(
                        capture.Evidence.ByteLength,
                        capture.Evidence.Sha256,
                        IdentityProviderName,
                        capture.Evidence.FilesystemIdentity)),
            OutputArtifactNativeCaptureStatus.Missing =>
                OutputArtifactEvidenceCaptureResult.Missing(),
            OutputArtifactNativeCaptureStatus.Unsupported =>
                OutputArtifactEvidenceCaptureResult.Unsupported(),
            _ => OutputArtifactEvidenceCaptureResult.Failed()
        };
    }

    public async Task<OutputArtifactSafeDeleteResult> DeleteTemporaryIfOwnedAsync(
        string temporaryPath,
        OutputArtifactTemporaryClaim temporaryClaim,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(temporaryClaim);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_fileSystem.IsSupported)
        {
            return OutputArtifactSafeDeleteResult.Unsupported();
        }

        if (temporaryClaim is not WindowsOutputArtifactTemporaryClaim windowsClaim)
        {
            return OutputArtifactSafeDeleteResult.Unproven();
        }

        var result = await _fileSystem
            .DeleteIfIdentityMatchesAsync(
                temporaryPath,
                windowsClaim.FilesystemIdentity,
                cancellationToken)
            .ConfigureAwait(false);
        return MapSafeDeleteResult(result);
    }

    public async Task<OutputArtifactSafeDeleteResult> DeleteIfOwnedAsync(
        string candidatePath,
        DownloadOutputArtifactProvenance provenance,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentNullException.ThrowIfNull(provenance);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_fileSystem.IsSupported)
        {
            return OutputArtifactSafeDeleteResult.Unsupported();
        }

        if (!HasUsableEvidence(candidatePath, provenance))
        {
            return OutputArtifactSafeDeleteResult.Unproven();
        }

        var result = await _fileSystem
            .DeleteIfEvidenceMatchesAsync(
                candidatePath,
                new OutputArtifactNativeEvidence(
                    provenance.ByteLength,
                    provenance.Sha256,
                    provenance.FilesystemIdentity),
                cancellationToken)
            .ConfigureAwait(false);
        return MapSafeDeleteResult(result);
    }

    public async Task<bool> VerifyPublishedObjectIdentityAsync(
        string destinationPath,
        OutputArtifactPublicationEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_fileSystem.IsSupported || !HasUsableEvidence(evidence))
        {
            return false;
        }

        return await _fileSystem
            .VerifyIdentityAndLengthAsync(
                destinationPath,
                new OutputArtifactNativeEvidence(
                    evidence.ByteLength,
                    evidence.Sha256,
                    evidence.FilesystemIdentity),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool HasUsableEvidence(
        string candidatePath,
        DownloadOutputArtifactProvenance provenance)
    {
        if (!HasUsableEvidence(
                new OutputArtifactPublicationEvidence(
                    provenance.ByteLength,
                    provenance.Sha256,
                    provenance.IdentityProvider,
                    provenance.FilesystemIdentity))
            || string.IsNullOrWhiteSpace(provenance.CanonicalPath))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(provenance.CanonicalPath))
            {
                return false;
            }

            var canonicalCandidate = Path.GetFullPath(candidatePath);
            var canonicalProvenance = Path.GetFullPath(provenance.CanonicalPath);
            return string.Equals(
                canonicalCandidate,
                canonicalProvenance,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasUsableEvidence(
        OutputArtifactPublicationEvidence evidence)
    {
        return string.Equals(
                   evidence.IdentityProvider,
                   IdentityProviderName,
                   StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(evidence.FilesystemIdentity)
               && evidence.ByteLength >= 0
               && IsCanonicalSha256(evidence.Sha256);
    }

    private static bool IsCanonicalSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if ((character is < '0' or > '9')
                && (character is < 'a' or > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static OutputArtifactSafeDeleteResult MapSafeDeleteResult(
        OutputArtifactNativeSafeDeleteResult result)
    {
        return result.Status switch
        {
            OutputArtifactNativeSafeDeleteStatus.Deleted =>
                OutputArtifactSafeDeleteResult.DeletedResult(),
            OutputArtifactNativeSafeDeleteStatus.Missing =>
                OutputArtifactSafeDeleteResult.Missing(),
            OutputArtifactNativeSafeDeleteStatus.Replaced =>
                OutputArtifactSafeDeleteResult.Replaced(),
            OutputArtifactNativeSafeDeleteStatus.Modified =>
                OutputArtifactSafeDeleteResult.Modified(),
            OutputArtifactNativeSafeDeleteStatus.Unsupported =>
                OutputArtifactSafeDeleteResult.Unsupported(),
            _ => OutputArtifactSafeDeleteResult.Failed()
        };
    }

    private sealed record WindowsOutputArtifactTemporaryClaim(
        string FilesystemIdentity) : OutputArtifactTemporaryClaim;
}

/// <summary>
/// Internal seam between ownership policy and native handles. The Windows
/// implementation keeps the handle entirely inside the two operations so a
/// caller cannot validate one object and path-delete another.
/// </summary>
internal interface IOutputArtifactNativeFileSystem
{
    bool IsSupported { get; }

    OutputArtifactNativeIdentityCaptureResult CaptureIdentity(
        SafeFileHandle handle);

    Task<OutputArtifactNativeCaptureResult> CaptureEvidenceAsync(
        string path,
        CancellationToken cancellationToken);

    Task<OutputArtifactNativeSafeDeleteResult> DeleteIfEvidenceMatchesAsync(
        string path,
        OutputArtifactNativeEvidence expectedEvidence,
        CancellationToken cancellationToken);

    Task<OutputArtifactNativeSafeDeleteResult> DeleteIfIdentityMatchesAsync(
        string path,
        string expectedFilesystemIdentity,
        CancellationToken cancellationToken);

    Task<bool> VerifyIdentityAndLengthAsync(
        string path,
        OutputArtifactNativeEvidence expectedEvidence,
        CancellationToken cancellationToken);
}

internal enum OutputArtifactNativeIdentityCaptureStatus
{
    Captured,
    Unsupported,
    Failed
}

internal sealed record OutputArtifactNativeIdentityCaptureResult(
    OutputArtifactNativeIdentityCaptureStatus Status,
    string? FilesystemIdentity)
{
    public static OutputArtifactNativeIdentityCaptureResult Captured(string filesystemIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filesystemIdentity);
        return new OutputArtifactNativeIdentityCaptureResult(
            OutputArtifactNativeIdentityCaptureStatus.Captured,
            filesystemIdentity);
    }

    public static OutputArtifactNativeIdentityCaptureResult Unsupported() =>
        new(OutputArtifactNativeIdentityCaptureStatus.Unsupported, null);

    public static OutputArtifactNativeIdentityCaptureResult Failed() =>
        new(OutputArtifactNativeIdentityCaptureStatus.Failed, null);
}

internal enum OutputArtifactNativeCaptureStatus
{
    Captured,
    Missing,
    Unsupported,
    Failed
}

internal sealed record OutputArtifactNativeCaptureResult(
    OutputArtifactNativeCaptureStatus Status,
    OutputArtifactNativeEvidence? Evidence)
{
    public static OutputArtifactNativeCaptureResult Captured(
        OutputArtifactNativeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new OutputArtifactNativeCaptureResult(
            OutputArtifactNativeCaptureStatus.Captured,
            evidence);
    }

    public static OutputArtifactNativeCaptureResult Missing() =>
        new(OutputArtifactNativeCaptureStatus.Missing, null);

    public static OutputArtifactNativeCaptureResult Unsupported() =>
        new(OutputArtifactNativeCaptureStatus.Unsupported, null);

    public static OutputArtifactNativeCaptureResult Failed() =>
        new(OutputArtifactNativeCaptureStatus.Failed, null);
}

internal enum OutputArtifactNativeSafeDeleteStatus
{
    Deleted,
    Missing,
    Replaced,
    Modified,
    Unsupported,
    Failed
}

internal sealed record OutputArtifactNativeSafeDeleteResult(
    OutputArtifactNativeSafeDeleteStatus Status)
{
    public static OutputArtifactNativeSafeDeleteResult Deleted() =>
        new(OutputArtifactNativeSafeDeleteStatus.Deleted);

    public static OutputArtifactNativeSafeDeleteResult Missing() =>
        new(OutputArtifactNativeSafeDeleteStatus.Missing);

    public static OutputArtifactNativeSafeDeleteResult Replaced() =>
        new(OutputArtifactNativeSafeDeleteStatus.Replaced);

    public static OutputArtifactNativeSafeDeleteResult Modified() =>
        new(OutputArtifactNativeSafeDeleteStatus.Modified);

    public static OutputArtifactNativeSafeDeleteResult Unsupported() =>
        new(OutputArtifactNativeSafeDeleteStatus.Unsupported);

    public static OutputArtifactNativeSafeDeleteResult Failed() =>
        new(OutputArtifactNativeSafeDeleteStatus.Failed);
}

internal sealed record OutputArtifactNativeEvidence(
    long ByteLength,
    string Sha256,
    string FilesystemIdentity);
