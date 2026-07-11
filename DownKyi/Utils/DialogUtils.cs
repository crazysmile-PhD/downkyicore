using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace DownKyi.Utils;

public static class DialogUtils
{
    private static readonly string DefaultDirectory = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly FilePickerFileType[] VideoFileTypes =
    {
        new("select") { Patterns = new[] { "*.mp4" }, MimeTypes = new[] { "video/mp4" } }
    };

    /// <summary>
    /// 弹出选择文件夹弹窗
    /// </summary>
    /// <returns></returns>
    public static async Task<string?> SetDownloadDirectory()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow?.StorageProvider is not { } provider)
            throw new NullReferenceException("Missing StorageProvider instance.");
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择文件夹",
            SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(new Uri(DefaultDirectory)).ConfigureAwait(true),
            AllowMultiple = false
        }).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// 选择视频dialog
    /// </summary>
    /// <returns></returns>
    public static async Task<string?> SelectVideoFile()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow?.StorageProvider is not { } provider)
            throw new NullReferenceException("Missing StorageProvider instance.");
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频",
            SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(new Uri(DefaultDirectory)).ConfigureAwait(true),
            AllowMultiple = false,
            FileTypeFilter = VideoFileTypes
        }).ConfigureAwait(true);

        // 选择文件
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// 选择多个视频dialog
    /// </summary>
    /// <returns></returns>
    public static async Task<string[]?> SelectMultiVideoFile()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow?.StorageProvider is not { } provider)
            throw new NullReferenceException("Missing StorageProvider instance.");
        var files = await provider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "选择视频",
                SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(new Uri(DefaultDirectory)).ConfigureAwait(true),
                AllowMultiple = true,
                FileTypeFilter = VideoFileTypes
            }
        ).ConfigureAwait(true);

        // 选择文件
        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToArray();
    }
}
