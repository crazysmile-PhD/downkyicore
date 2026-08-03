using System.Text;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Core.Aria2cNet.Server;

internal sealed class AriaRpcSecretFile : IDisposable
{
    private readonly ILogger _logger;
    private int _disposed;

    private AriaRpcSecretFile(string path, ILogger logger)
    {
        Path = path;
        _logger = logger;
    }

    public string Path { get; }

    public static AriaRpcSecretFile Create(
        string directory,
        string secret,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(logger);
        if (secret.Any(char.IsControl))
        {
            throw new ArgumentException("The aria2 RPC secret contains a control character.", nameof(secret));
        }

        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(
            directory,
            $".rpc-{Guid.NewGuid():N}.conf");
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write("rpc-secret=");
            writer.WriteLine(secret);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        return new AriaRpcSecretFile(path, logger);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!File.Exists(Path))
            {
                return;
            }

            using (var stream = new FileStream(
                       Path,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.SetLength(0);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(Path);
        }
        catch (IOException exception)
        {
            _logger.LogErrorMessage("The temporary aria2 RPC secret file could not be removed.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogErrorMessage("Removal of the temporary aria2 RPC secret file was denied.", exception);
        }
    }
}
