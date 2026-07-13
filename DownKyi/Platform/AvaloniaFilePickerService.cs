using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using DownKyi.Application.Desktop;

namespace DownKyi.Platform;

internal sealed class AvaloniaFilePickerService : IFilePickerService
{
    private static readonly string DefaultDirectory = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly FilePickerFileType[] VideoFileTypes =
    {
        new("video") { Patterns = new[] { "*.mp4" }, MimeTypes = new[] { "video/mp4" } }
    };

    public async Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default)
    {
        var provider = GetStorageProvider();
        cancellationToken.ThrowIfCancellationRequested();
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择文件夹",
            SuggestedStartLocation = await provider
                .TryGetFolderFromPathAsync(new Uri(DefaultDirectory))
                .ConfigureAwait(true),
            AllowMultiple = false
        }).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default)
    {
        var files = await SelectVideoFilesAsync(allowMultiple: false, cancellationToken).ConfigureAwait(true);
        return files.Count > 0 ? files[0] : null;
    }

    public async Task<IReadOnlyList<string>> SelectVideosAsync(CancellationToken cancellationToken = default)
    {
        return await SelectVideoFilesAsync(allowMultiple: true, cancellationToken).ConfigureAwait(true);
    }

    private static async Task<IReadOnlyList<string>> SelectVideoFilesAsync(
        bool allowMultiple,
        CancellationToken cancellationToken)
    {
        var provider = GetStorageProvider();
        cancellationToken.ThrowIfCancellationRequested();
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频",
            SuggestedStartLocation = await provider
                .TryGetFolderFromPathAsync(new Uri(DefaultDirectory))
                .ConfigureAwait(true),
            AllowMultiple = allowMultiple,
            FileTypeFilter = VideoFileTypes
        }).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToArray();
    }

    private static IStorageProvider GetStorageProvider()
    {
        return App.Current.MainWindow.StorageProvider
               ?? throw new InvalidOperationException("Missing StorageProvider instance.");
    }
}
