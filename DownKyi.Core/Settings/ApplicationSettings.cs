using System.Collections.Immutable;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.FileName;

namespace DownKyi.Core.Settings;

public sealed record ApplicationSettings(
    int SchemaVersion,
    BasicApplicationSettings Basic,
    NetworkApplicationSettings Network,
    VideoApplicationSettings Video,
    DanmakuApplicationSettings Danmaku,
    AboutApplicationSettings About,
    UserApplicationSettings User,
    WindowApplicationSettings Window);

public sealed record BasicApplicationSettings(
    ThemeMode ThemeMode,
    AfterDownloadOperation AfterDownload,
    AllowStatus IsListenClipboard,
    AllowStatus IsAutoParseVideo,
    ParseScope ParseScope,
    AllowStatus IsAutoDownloadAll,
    DownloadFinishedSort DownloadFinishedSort,
    RepeatDownloadStrategy RepeatDownloadStrategy,
    bool RepeatFileAutoAddNumberSuffix);

public sealed record NetworkApplicationSettings(
    AllowStatus IsLiftingOfRegion,
    AllowStatus UseSsl,
    string UserAgent,
    Downloader Downloader,
    AllowStatus HighSpeedDownloadMode,
    NetworkProxy NetworkProxy,
    string CustomNetworkProxy,
    int MaxCurrentDownloads,
    int Split,
    AllowStatus IsHttpProxy,
    string HttpProxy,
    int HttpProxyListenPort,
    string AriaToken,
    string AriaHost,
    int AriaListenPort,
    AriaConfigLogLevel AriaLogLevel,
    int AriaSplit,
    int AriaMaxConnectionPerServer,
    int AriaMinSplitSize,
    int AriaMaxOverallDownloadLimit,
    int AriaMaxDownloadLimit,
    AriaConfigFileAllocation AriaFileAllocation,
    AllowStatus IsAriaHttpProxy,
    string AriaHttpProxy,
    int AriaHttpProxyListenPort);

public sealed record VideoApplicationSettings(
    int VideoCodecs,
    int Quality,
    int AudioQuality,
    int VideoParseType,
    AllowStatus IsTranscodingFlvToMp4,
    AllowStatus IsTranscodingAacToMp3,
    FfmpegHardwareAcceleration FfmpegHardwareAcceleration,
    int FfmpegMaxParallelJobs,
    string SaveVideoRootPath,
    ImmutableArray<string> HistoryVideoRootPaths,
    AllowStatus IsUseSaveVideoRootPath,
    VideoContentApplicationSettings Content,
    ImmutableArray<FileNamePart> FileNameParts,
    string FileNamePartTimeFormat,
    OrderFormat OrderFormat);

public sealed record VideoContentApplicationSettings(
    bool DownloadAudio,
    bool DownloadVideo,
    bool DownloadDanmaku,
    bool DownloadSubtitle,
    bool DownloadCover,
    bool GenerateMovieMetadata);

public sealed record DanmakuApplicationSettings(
    AllowStatus TopFilter,
    AllowStatus BottomFilter,
    AllowStatus ScrollFilter,
    AllowStatus IsCustomResolution,
    int ScreenWidth,
    int ScreenHeight,
    string FontName,
    int FontSize,
    int LineCount,
    DanmakuLayoutAlgorithm LayoutAlgorithm);

public sealed record AboutApplicationSettings(
    AllowStatus IsReceiveBetaVersion,
    AllowStatus AutoUpdateWhenLaunch,
    string SkipVersionOnLaunch);

public sealed record UserApplicationSettings(
    long Mid,
    string Name,
    bool IsLogin,
    bool IsVip,
    string ImgKey,
    string SubKey);

public sealed record WindowApplicationSettings(
    double Width,
    double Height,
    double X,
    double Y);

internal sealed record SettingsValidationResult(
    ApplicationSettings Settings,
    ImmutableArray<string> Corrections);

internal static class ApplicationSettingsValidator
{
    public const int CurrentSchemaVersion = 1;
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public static SettingsValidationResult Validate(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var corrections = ImmutableArray.CreateBuilder<string>();
        var basic = settings.Basic with
        {
            ThemeMode = EnumValue(settings.Basic.ThemeMode, ThemeMode.Default, "Basic.ThemeMode", corrections),
            AfterDownload = EnumValue(settings.Basic.AfterDownload, AfterDownloadOperation.None, "Basic.AfterDownload", corrections),
            IsListenClipboard = AllowValue(settings.Basic.IsListenClipboard, AllowStatus.Yes, "Basic.IsListenClipboard", corrections),
            IsAutoParseVideo = AllowValue(settings.Basic.IsAutoParseVideo, AllowStatus.No, "Basic.IsAutoParseVideo", corrections),
            ParseScope = EnumValue(settings.Basic.ParseScope, ParseScope.None, "Basic.ParseScope", corrections),
            IsAutoDownloadAll = AllowValue(settings.Basic.IsAutoDownloadAll, AllowStatus.No, "Basic.IsAutoDownloadAll", corrections),
            DownloadFinishedSort = EnumValue(settings.Basic.DownloadFinishedSort, DownloadFinishedSort.DownloadAsc, "Basic.DownloadFinishedSort", corrections),
            RepeatDownloadStrategy = EnumValue(settings.Basic.RepeatDownloadStrategy, RepeatDownloadStrategy.Ask, "Basic.RepeatDownloadStrategy", corrections)
        };
        var networkProxy = EnumValue(settings.Network.NetworkProxy, NetworkProxy.None, "Network.NetworkProxy", corrections);
        var customProxy = settings.Network.CustomNetworkProxy;
        if (networkProxy == NetworkProxy.Custom
            && !string.IsNullOrWhiteSpace(customProxy)
            && !IsHttpUri(customProxy))
        {
            corrections.Add("Network.CustomNetworkProxy");
            networkProxy = NetworkProxy.None;
            customProxy = string.Empty;
        }

        var network = settings.Network with
        {
            IsLiftingOfRegion = AllowValue(settings.Network.IsLiftingOfRegion, AllowStatus.Yes, "Network.IsLiftingOfRegion", corrections),
            UseSsl = AllowValue(settings.Network.UseSsl, AllowStatus.Yes, "Network.UseSsl", corrections),
            UserAgent = TextValue(settings.Network.UserAgent, DefaultUserAgent, "Network.UserAgent", corrections),
            Downloader = EnumValue(settings.Network.Downloader, Downloader.Aria, "Network.Downloader", corrections),
            HighSpeedDownloadMode = AllowValue(settings.Network.HighSpeedDownloadMode, AllowStatus.No, "Network.HighSpeedDownloadMode", corrections),
            NetworkProxy = networkProxy,
            CustomNetworkProxy = customProxy,
            MaxCurrentDownloads = Range(settings.Network.MaxCurrentDownloads, 1, 16, 3, "Network.MaxCurrentDownloads", corrections),
            Split = Range(settings.Network.Split, 1, 32, 8, "Network.Split", corrections),
            IsHttpProxy = AllowValue(settings.Network.IsHttpProxy, AllowStatus.No, "Network.IsHttpProxy", corrections),
            HttpProxy = settings.Network.HttpProxy ?? string.Empty,
            HttpProxyListenPort = Range(settings.Network.HttpProxyListenPort, 0, 65535, 0, "Network.HttpProxyListenPort", corrections),
            AriaToken = settings.Network.AriaToken ?? "downkyi",
            AriaHost = IsHttpUri(settings.Network.AriaHost) ? settings.Network.AriaHost : Corrected("Network.AriaHost", "http://localhost", corrections),
            AriaListenPort = Range(settings.Network.AriaListenPort, 1, 65535, 35076, "Network.AriaListenPort", corrections),
            AriaLogLevel = EnumValue(settings.Network.AriaLogLevel, AriaConfigLogLevel.WARN, "Network.AriaLogLevel", corrections),
            AriaSplit = Range(settings.Network.AriaSplit, 1, 16, 5, "Network.AriaSplit", corrections),
            AriaMaxConnectionPerServer = Range(settings.Network.AriaMaxConnectionPerServer, 1, 16, 8, "Network.AriaMaxConnectionPerServer", corrections),
            AriaMinSplitSize = Range(settings.Network.AriaMinSplitSize, 1, 1024, 10, "Network.AriaMinSplitSize", corrections),
            AriaMaxOverallDownloadLimit = NonNegative(settings.Network.AriaMaxOverallDownloadLimit, "Network.AriaMaxOverallDownloadLimit", corrections),
            AriaMaxDownloadLimit = NonNegative(settings.Network.AriaMaxDownloadLimit, "Network.AriaMaxDownloadLimit", corrections),
            AriaFileAllocation = EnumValue(settings.Network.AriaFileAllocation, AriaConfigFileAllocation.NONE, "Network.AriaFileAllocation", corrections),
            IsAriaHttpProxy = AllowValue(settings.Network.IsAriaHttpProxy, AllowStatus.No, "Network.IsAriaHttpProxy", corrections),
            AriaHttpProxy = settings.Network.AriaHttpProxy ?? string.Empty,
            AriaHttpProxyListenPort = Range(settings.Network.AriaHttpProxyListenPort, 0, 65535, 0, "Network.AriaHttpProxyListenPort", corrections)
        };
        var video = settings.Video with
        {
            VideoCodecs = settings.Video.VideoCodecs < 0 ? Corrected("Video.VideoCodecs", 7, corrections) : settings.Video.VideoCodecs,
            Quality = settings.Video.Quality <= 0 ? Corrected("Video.Quality", 120, corrections) : settings.Video.Quality,
            AudioQuality = settings.Video.AudioQuality <= 0 ? Corrected("Video.AudioQuality", 30280, corrections) : settings.Video.AudioQuality,
            VideoParseType = Range(settings.Video.VideoParseType, 0, 1, 0, "Video.VideoParseType", corrections),
            IsTranscodingFlvToMp4 = AllowValue(settings.Video.IsTranscodingFlvToMp4, AllowStatus.Yes, "Video.IsTranscodingFlvToMp4", corrections),
            IsTranscodingAacToMp3 = AllowValue(settings.Video.IsTranscodingAacToMp3, AllowStatus.Yes, "Video.IsTranscodingAacToMp3", corrections),
            FfmpegHardwareAcceleration = EnumValue(settings.Video.FfmpegHardwareAcceleration, FfmpegHardwareAcceleration.Auto, "Video.FfmpegHardwareAcceleration", corrections),
            FfmpegMaxParallelJobs = Range(settings.Video.FfmpegMaxParallelJobs, 1, 4, 1, "Video.FfmpegMaxParallelJobs", corrections),
            FileNamePartTimeFormat = TextValue(settings.Video.FileNamePartTimeFormat, "yyyy-MM-dd", "Video.FileNamePartTimeFormat", corrections),
            OrderFormat = EnumValue(settings.Video.OrderFormat, OrderFormat.Natural, "Video.OrderFormat", corrections)
        };
        var danmaku = settings.Danmaku with
        {
            TopFilter = AllowValue(settings.Danmaku.TopFilter, AllowStatus.No, "Danmaku.TopFilter", corrections),
            BottomFilter = AllowValue(settings.Danmaku.BottomFilter, AllowStatus.No, "Danmaku.BottomFilter", corrections),
            ScrollFilter = AllowValue(settings.Danmaku.ScrollFilter, AllowStatus.No, "Danmaku.ScrollFilter", corrections),
            IsCustomResolution = AllowValue(settings.Danmaku.IsCustomResolution, AllowStatus.No, "Danmaku.IsCustomResolution", corrections),
            ScreenWidth = Range(settings.Danmaku.ScreenWidth, 1, 16384, 1920, "Danmaku.ScreenWidth", corrections),
            ScreenHeight = Range(settings.Danmaku.ScreenHeight, 1, 16384, 1080, "Danmaku.ScreenHeight", corrections),
            FontName = TextValue(settings.Danmaku.FontName, "黑体", "Danmaku.FontName", corrections),
            FontSize = Range(settings.Danmaku.FontSize, 1, 500, 50, "Danmaku.FontSize", corrections),
            LineCount = Range(settings.Danmaku.LineCount, 0, 1000, 0, "Danmaku.LineCount", corrections),
            LayoutAlgorithm = EnumValue(settings.Danmaku.LayoutAlgorithm, DanmakuLayoutAlgorithm.Sync, "Danmaku.LayoutAlgorithm", corrections)
        };
        var about = settings.About with
        {
            IsReceiveBetaVersion = AllowValue(settings.About.IsReceiveBetaVersion, AllowStatus.No, "About.IsReceiveBetaVersion", corrections),
            AutoUpdateWhenLaunch = AllowValue(settings.About.AutoUpdateWhenLaunch, AllowStatus.No, "About.AutoUpdateWhenLaunch", corrections),
            SkipVersionOnLaunch = Version.TryParse(settings.About.SkipVersionOnLaunch, out _)
                ? settings.About.SkipVersionOnLaunch
                : string.Empty
        };
        var user = settings.User with
        {
            Mid = settings.User.Mid < -1 ? Corrected("User.Mid", -1L, corrections) : settings.User.Mid,
            Name = settings.User.Name ?? string.Empty,
            ImgKey = settings.User.ImgKey ?? string.Empty,
            SubKey = settings.User.SubKey ?? string.Empty
        };
        var window = settings.Window with
        {
            Width = FiniteRange(settings.Window.Width, 320, 16384, 1100, "Window.Width", corrections),
            Height = FiniteRange(settings.Window.Height, 240, 16384, 750, "Window.Height", corrections),
            X = FiniteOrNaN(settings.Window.X, "Window.X", corrections),
            Y = FiniteOrNaN(settings.Window.Y, "Window.Y", corrections)
        };

        var schemaVersion = settings.SchemaVersion == CurrentSchemaVersion
            ? settings.SchemaVersion
            : Corrected("SchemaVersion", CurrentSchemaVersion, corrections);
        return new SettingsValidationResult(
            settings with
            {
                SchemaVersion = schemaVersion,
                Basic = basic,
                Network = network,
                Video = video,
                Danmaku = danmaku,
                About = about,
                User = user,
                Window = window
            },
            corrections.ToImmutable());
    }

    private static AllowStatus AllowValue(
        AllowStatus value,
        AllowStatus fallback,
        string field,
        ImmutableArray<string>.Builder corrections)
    {
        return value is AllowStatus.Yes or AllowStatus.No
            ? value
            : Corrected(field, fallback, corrections);
    }

    private static T EnumValue<T>(
        T value,
        T fallback,
        string field,
        ImmutableArray<string>.Builder corrections)
        where T : struct, Enum
    {
        return Enum.IsDefined(value) ? value : Corrected(field, fallback, corrections);
    }

    private static int Range(
        int value,
        int minimum,
        int maximum,
        int fallback,
        string field,
        ImmutableArray<string>.Builder corrections)
    {
        return value >= minimum && value <= maximum ? value : Corrected(field, fallback, corrections);
    }

    private static int NonNegative(
        int value,
        string field,
        ImmutableArray<string>.Builder corrections)
    {
        return value >= 0 ? value : Corrected(field, 0, corrections);
    }

    private static double FiniteRange(
        double value,
        double minimum,
        double maximum,
        double fallback,
        string field,
        ImmutableArray<string>.Builder corrections)
    {
        return double.IsFinite(value) && value >= minimum && value <= maximum
            ? value
            : Corrected(field, fallback, corrections);
    }

    private static double FiniteOrNaN(
        double value,
        string field,
        ImmutableArray<string>.Builder corrections)
    {
        return double.IsFinite(value) || double.IsNaN(value)
            ? value
            : Corrected(field, double.NaN, corrections);
    }

    private static string TextValue(
        string? value,
        string fallback,
        string field,
        ImmutableArray<string>.Builder corrections)
    {
        return string.IsNullOrWhiteSpace(value) ? Corrected(field, fallback, corrections) : value;
    }

    private static bool IsHttpUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https";
    }

    private static T Corrected<T>(
        string field,
        T fallback,
        ImmutableArray<string>.Builder corrections)
    {
        corrections.Add(field);
        return fallback;
    }
}
