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
    {
    }

    public AtomicOutputPublisher(IOutputArtifactOwnershipProvider ownershipProvider)
    {
        _ownershipProvider = ownershipProvider
            ?? throw new ArgumentNullException(nameof(ownershipProvider));
    }

    internal AtomicOutputPublisher(Action<string> beforePublish)
    {
        _beforePublish = beforePublish ?? throw new ArgumentNullException(nameof(beforePublish));
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
        var temporaryPath = CreateTemporaryFile(destinationPath);
        try
        {
            await writeTemporaryAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            OutputArtifactPublicationEvidence? publicationEvidence = null;
            if (_ownershipProvider != null)
            {
                var captured = await _ownershipProvider
                    .CapturePublicationEvidenceAsync(temporaryPath, cancellationToken)
                    .ConfigureAwait(false);
                publicationEvidence = captured.Succeeded ? captured.Evidence : null;
            }

            _beforePublish?.Invoke(destinationPath);
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
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
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private static string CreateTemporaryFile(string destinationPath)
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
                    FileAccess.Write,
                    FileShare.None);
                return temporaryPath;
            }
            catch (IOException) when (attempt < MaximumTemporaryPathAttempts - 1)
            {
                continue;
            }
        }

        throw new IOException("A unique temporary output file could not be created.");
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

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
    }
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
