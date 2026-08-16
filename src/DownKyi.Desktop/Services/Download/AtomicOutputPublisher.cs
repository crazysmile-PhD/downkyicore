using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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

    public AtomicOutputPublisher()
    {
    }

    internal AtomicOutputPublisher(Action<string> beforePublish)
    {
        _beforePublish = beforePublish ?? throw new ArgumentNullException(nameof(beforePublish));
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
            _beforePublish?.Invoke(destinationPath);
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }
            catch (IOException exception) when (File.Exists(destinationPath))
            {
                return AtomicOutputPublishResult.DestinationCollision(exception);
            }

            return AtomicOutputPublishResult.Published();
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
            }
        }

        throw new IOException("A unique temporary output file could not be created.");
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record AtomicOutputPublishResult(
    bool Succeeded,
    bool IsDestinationCollision,
    IOException? Error)
{
    public static AtomicOutputPublishResult Published() => new(true, false, null);

    public static AtomicOutputPublishResult DestinationCollision(IOException error) =>
        new(false, true, error);
}
