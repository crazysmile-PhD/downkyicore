using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;

namespace DownKyi.Services.Download;

internal interface IAtomicOutputPublisher
{
    Task<AtomicOutputPublishResult> PublishAsync(
        string destinationPath,
        Func<string, CancellationToken, Task> writeTemporaryAsync,
        CancellationToken cancellationToken);
}

internal sealed class AtomicOutputPublisher : IAtomicOutputPublisher
{
    private const int MaximumTemporaryPathAttempts = 8;
    private readonly Action<string>? _beforePublish;
    private readonly IOutputArtifactOwnershipProvider? _ownershipProvider;

    public AtomicOutputPublisher()
        : this(new WindowsOutputArtifactOwnershipProvider())
    {
    }

    public AtomicOutputPublisher(IOutputArtifactOwnershipProvider ownershipProvider)
    {
        _ownershipProvider = ownershipProvider
            ?? throw new ArgumentNullException(nameof(ownershipProvider));
    }

    internal AtomicOutputPublisher(Action<string> beforePublish)
        : this(beforePublish, new WindowsOutputArtifactOwnershipProvider())
    {
    }

    internal AtomicOutputPublisher(
        Action<string> beforePublish,
        IOutputArtifactOwnershipProvider ownershipProvider)
    {
        _beforePublish = beforePublish ?? throw new ArgumentNullException(nameof(beforePublish));
        _ownershipProvider = ownershipProvider
            ?? throw new ArgumentNullException(nameof(ownershipProvider));
    }

    public async Task<AtomicOutputPublishResult> PublishAsync(
        string destinationPath,
        Func<string, CancellationToken, Task> writeTemporaryAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(writeTemporaryAsync);
        var temporary = CreateTemporaryFile(destinationPath);
        try
        {
            await writeTemporaryAsync(temporary.Path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            OutputArtifactPublicationEvidence? publicationEvidence = null;
            if (_ownershipProvider != null && temporary.Claim is not null)
            {
                var captured = await _ownershipProvider
                    .CapturePublicationEvidenceAsync(
                        temporary.Path,
                        temporary.Claim,
                        cancellationToken)
                    .ConfigureAwait(false);
                publicationEvidence = captured.Succeeded ? captured.Evidence : null;
            }

            _beforePublish?.Invoke(destinationPath);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary.Path, destinationPath, overwrite: false);
            }
            catch (IOException exception) when (File.Exists(destinationPath))
            {
                return AtomicOutputPublishResult.DestinationCollision(exception);
            }

            if (publicationEvidence is not null
                && !await VerifyPublishedIdentityAsync(
                        destinationPath,
                        publicationEvidence)
                    .ConfigureAwait(false))
            {
                // The final name no longer resolves to the object whose hash
                // was captured before the move. The file remains published,
                // but it must stay untracked and therefore undeletable.
                publicationEvidence = null;
            }

            return AtomicOutputPublishResult.Published(publicationEvidence);
        }
        finally
        {
            await DeleteTemporaryIfOwnedAsync(temporary).ConfigureAwait(false);
        }
    }

    private TemporaryOutput CreateTemporaryFile(string destinationPath)
    {
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new IOException("The output destination has no directory.");
        var name = Path.GetFileNameWithoutExtension(fullDestinationPath);
        var extension = Path.GetExtension(fullDestinationPath);
        for (var attempt = 0; attempt < MaximumTemporaryPathAttempts; attempt++)
        {
            var temporaryPath = Path.Combine(
                directory,
                $".{name}-{Guid.NewGuid():N}.downkyi-tmp{extension}");
            try
            {
                using var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None);
                var claim = _ownershipProvider?.ClaimTemporaryObject(stream.SafeFileHandle);
                return new TemporaryOutput(
                    temporaryPath,
                    claim is { Succeeded: true } ? claim.Claim : null);
            }
            catch (IOException) when (attempt < MaximumTemporaryPathAttempts - 1)
            {
                continue;
            }
        }

        throw new IOException("A unique temporary output file could not be created.");
    }

    private async Task DeleteTemporaryIfOwnedAsync(TemporaryOutput temporary)
    {
        if (_ownershipProvider is null || temporary.Claim is null)
        {
            return;
        }

        try
        {
            await _ownershipProvider
                .DeleteTemporaryIfOwnedAsync(
                    temporary.Path,
                    temporary.Claim,
                    CancellationToken.None)
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

    private async Task<bool> VerifyPublishedIdentityAsync(
        string destinationPath,
        OutputArtifactPublicationEvidence publicationEvidence)
    {
        if (_ownershipProvider is null)
        {
            return false;
        }

        try
        {
            // Publication is irreversible at this point. Do not let a later
            // cancellation change its success outcome; absent verification is
            // represented by null evidence and an untracked output.
            return await _ownershipProvider
                .VerifyPublishedObjectIdentityAsync(
                    destinationPath,
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

    private sealed record TemporaryOutput(
        string Path,
        OutputArtifactTemporaryClaim? Claim);
}

internal sealed record AtomicOutputPublishResult(
    bool Succeeded,
    bool IsDestinationCollision,
    IOException? Error,
    OutputArtifactPublicationEvidence? PublicationEvidence)
{
    public static AtomicOutputPublishResult Published(
        OutputArtifactPublicationEvidence? publicationEvidence = null) =>
        new(true, false, null, publicationEvidence);

    public static AtomicOutputPublishResult DestinationCollision(IOException error) =>
        new(false, true, error, null);
}
