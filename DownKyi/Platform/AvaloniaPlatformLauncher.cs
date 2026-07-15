using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DownKyi.Application.Desktop;
using DownKyi.Core.Logging;
using Microsoft.Extensions.Logging;

namespace DownKyi.Platform;

internal sealed class AvaloniaPlatformLauncher : IPlatformLauncher
{
    private readonly ILogger<AvaloniaPlatformLauncher> _logger;

    public AvaloniaPlatformLauncher(ILogger<AvaloniaPlatformLauncher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        return OpenStorageItemAsync(path, isFolder: false, cancellationToken);
    }

    public Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        return OpenStorageItemAsync(path, isFolder: true, cancellationToken);
    }

    public Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri)
        {
            _logger.LogWarningMessage("A relative URI cannot be opened by the desktop launcher.");
            return Task.FromResult(false);
        }

        return Task.FromResult(TryLaunch(uri.AbsoluteUri));
    }

    private async Task<bool> OpenStorageItemAsync(
        string path,
        bool isFolder,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var topLevel = TopLevel.GetTopLevel(App.Current.MainWindow);
            if (topLevel == null)
            {
                _logger.LogWarningMessage("The desktop storage provider is unavailable.");
                return false;
            }

            var absolutePath = Path.GetFullPath(path, AppContext.BaseDirectory);
            var targetUri = new Uri(absolutePath, UriKind.Absolute);
            IStorageItem? storageItem;
            if (isFolder)
            {
                storageItem = await topLevel.StorageProvider
                    .TryGetFolderFromPathAsync(targetUri)
                    .ConfigureAwait(true);
            }
            else
            {
                storageItem = await topLevel.StorageProvider
                    .TryGetFileFromPathAsync(targetUri)
                    .ConfigureAwait(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var localPath = storageItem?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                _logger.LogWarningMessage(isFolder
                    ? "The requested folder is unavailable."
                    : "The requested file is unavailable.");
                return false;
            }

            return TryLaunch(localPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is ArgumentException or IOException
            or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            _logger.LogErrorMessage(isFolder
                ? "Opening a folder failed."
                : "Opening a file failed.", e);
            return false;
        }
    }

    private bool TryLaunch(string target)
    {
        try
        {
            var startInfo = CreateStartInfo(target);
            if (startInfo == null)
            {
                _logger.LogWarningMessage("The current operating system has no desktop launcher adapter.");
                return false;
            }

            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            _logger.LogErrorMessage($"The operating system rejected a desktop launch request ({e.GetType().Name}).");
            return false;
        }
    }

    private static ProcessStartInfo? CreateStartInfo(string target)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            var startInfo = CreateArgumentListStartInfo("open");
            startInfo.ArgumentList.Add(target);
            return startInfo;
        }

        if (OperatingSystem.IsLinux())
        {
            var startInfo = CreateArgumentListStartInfo("xdg-open");
            startInfo.ArgumentList.Add(target);
            return startInfo;
        }

        return null;
    }

    private static ProcessStartInfo CreateArgumentListStartInfo(string executable)
    {
        return new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }
}
