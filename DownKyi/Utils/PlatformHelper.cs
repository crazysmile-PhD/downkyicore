using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DownKyi.Core.Logging;
using DownKyi.Events;
using Prism.Events;

namespace DownKyi.Utils;

public static class PlatformHelper
{
    /// <summary>
    /// 打开文件夹
    /// </summary>
    /// <param name="folder">路径</param>
    /// <param name="eventAggregator"></param>
    public static async Task OpenFolder(string folder, IEventAggregator? eventAggregator = null)
    {
        var topLevel = TopLevel.GetTopLevel(App.Current.MainWindow);
        if (topLevel == null)
        {
            LogManager.Error(nameof(PlatformHelper), "无法获取顶层窗口，无法打开文件夹");
            eventAggregator?.GetEvent<MessageEvent>().Publish("无法获取顶层窗口，无法打开文件夹");
            return;
        }

        var openFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(folder));
        if (openFolder == null)
        {
            LogManager.Error(nameof(PlatformHelper), "无法获取文件夹路径");
            eventAggregator?.GetEvent<MessageEvent>().Publish("无法获取文件夹路径");
            return;
        }

        _ = await BclLauncher.LaunchFileAsync(openFolder);
    }

    /// <summary>
    /// 打开各种 (文件、url)
    /// </summary>
    /// <param name="filename">文件名</param>
    /// <param name="eventAggregator"></param>
    public static async Task Open(string filename, IEventAggregator? eventAggregator = null)
    {
        var topLevel = TopLevel.GetTopLevel(App.Current.MainWindow);
        if (topLevel == null)
        {
            LogManager.Error(nameof(PlatformHelper), "无法获取顶层窗口，无法打开文件");
            eventAggregator?.GetEvent<MessageEvent>().Publish("无法获取顶层窗口，无法打开文件");
            return;
        }

        var openFolder = await topLevel.StorageProvider.TryGetFileFromPathAsync(new Uri(filename));
        if (openFolder == null)
        {
            LogManager.Error(nameof(PlatformHelper), "无法获取文件路径");
            eventAggregator?.GetEvent<MessageEvent>().Publish("无法获取文件路径");
            return;
        }

        _ = await BclLauncher.LaunchFileAsync(openFolder);
    }

    public static async Task OpenUrl(string url, IEventAggregator? eventAggregator = null)
    {
        var topLevel = TopLevel.GetTopLevel(App.Current.MainWindow);
        if (topLevel == null)
        {
            LogManager.Error(nameof(PlatformHelper), "无法获取顶层窗口");
            eventAggregator?.GetEvent<MessageEvent>().Publish("无法获取顶层窗口");
            return;
        }

        _ = await BclLauncher.LaunchUriAsync(new Uri(url));
    }

    // https://github.com/AvaloniaUI/Avalonia/pull/19713 avalonia12才修复 暂时使用本地hack
    private static class BclLauncher
    {
        public static Task<bool> LaunchUriAsync(Uri uri)
        {
            _ = uri ?? throw new ArgumentNullException(nameof(uri));
            if (uri.IsAbsoluteUri)
            {
                return Task.FromResult(Exec(uri.AbsoluteUri));
            }

            return Task.FromResult(false);
        }

        /// <summary>
        /// This Process based implementation doesn't handle the case, when there is no app to handle link.
        /// It will still return true in this case.
        /// </summary>
        public static Task<bool> LaunchFileAsync(IStorageItem storageItem)
        {
            _ = storageItem ?? throw new ArgumentNullException(nameof(storageItem));
            if (storageItem.TryGetLocalPath() is { } localPath
                && CanOpenFileOrDirectory(localPath))
            {
                return Task.FromResult(Exec(localPath));
            }

            return Task.FromResult(false);
        }

        private static bool CanOpenFileOrDirectory(string localPath) => true;

        private static bool Exec(string urlOrFile)
        {
            if (OperatingSystem.IsLinux())
            {
                // If no associated application/json MimeType is found xdg-open opens return error
                // but it tries to open it anyway using the console editor (nano, vim, other..)
                var args = EscapeForShell(urlOrFile);
                ShellExecRaw($"xdg-open \\\"{args}\\\"", waitForExit: false);
                return true;
            }
            else if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                var info = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? urlOrFile : "open",
                    CreateNoWindow = true,
                    UseShellExecute = OperatingSystem.IsWindows()
                };
                // Using the argument list avoids having to escape spaces and other special 
                // characters that are part of valid macos file and folder paths.
                if (OperatingSystem.IsMacOS())
                    info.ArgumentList.Add(urlOrFile);
                using var process = Process.Start(info);
                return true;
            }
            else
            {
                return false;
            }
        }

        private static string EscapeForShell(string input) => Regex
            .Replace(input, "(?=[`~!#&*()|;'<>])", "\\")
            .Replace("\"", "\\\\\\\"");

        private static void ShellExecRaw(string cmd, bool waitForExit = true)
        {
            using var process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = $"-c \"{cmd}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            );
            if (waitForExit)
            {
                process?.WaitForExit();
            }
        }
    }
}