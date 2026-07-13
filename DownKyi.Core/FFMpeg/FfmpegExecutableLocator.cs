using System.Runtime.InteropServices;

namespace DownKyi.Core.FFMpeg;

internal static class FfmpegExecutableLocator
{
    public static string Ffmpeg => Locate("ffmpeg");

    public static string Ffprobe => Locate("ffprobe");

    private static string Locate(string name)
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{name}.exe"
            : name;
        var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", fileName);
        return File.Exists(bundledPath) ? bundledPath : name;
    }
}
