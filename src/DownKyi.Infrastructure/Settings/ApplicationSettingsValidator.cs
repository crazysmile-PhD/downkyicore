using System.Collections.Immutable;
using DownKyi.Application.Settings;
using DownKyi.Application.Versioning;
using DownKyi.Domain.Downloads;

namespace DownKyi.Infrastructure.Settings;

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
            UiLanguage = EnumValue(settings.Basic.UiLanguage, UiLanguage.ChineseSimplified, "Basic.UiLanguage", corrections),
            AfterDownload = EnumValueExcept(
                settings.Basic.AfterDownload,
                AfterDownloadOperation.NotSet,
                AfterDownloadOperation.None,
                "Basic.AfterDownload",
                corrections),
            IsListenClipboard = AllowValue(settings.Basic.IsListenClipboard, AllowStatus.Yes, "Basic.IsListenClipboard", corrections),
            DownloadFinishedSort = EnumValueExcept(
                settings.Basic.DownloadFinishedSort,
                DownloadFinishedSort.NotSet,
                DownloadFinishedSort.DownloadAsc,
                "Basic.DownloadFinishedSort",
                corrections)
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
            UserAgent = TextValue(settings.Network.UserAgent, DefaultUserAgent, "Network.UserAgent", corrections),
            HighSpeedDownloadMode = AllowValue(settings.Network.HighSpeedDownloadMode, AllowStatus.No, "Network.HighSpeedDownloadMode", corrections),
            NetworkProxy = networkProxy,
            CustomNetworkProxy = customProxy,
            MaxCurrentDownloads = Range(settings.Network.MaxCurrentDownloads, 1, 16, 3, "Network.MaxCurrentDownloads", corrections),
            Split = Range(settings.Network.Split, 1, 32, 8, "Network.Split", corrections),
            MaxOverallDownloadLimit = NonNegative(settings.Network.MaxOverallDownloadLimit, "Network.MaxOverallDownloadLimit", corrections),
            MaxDownloadLimit = NonNegative(settings.Network.MaxDownloadLimit, "Network.MaxDownloadLimit", corrections),
            IsHttpProxy = AllowValue(settings.Network.IsHttpProxy, AllowStatus.No, "Network.IsHttpProxy", corrections),
            HttpProxy = settings.Network.HttpProxy ?? string.Empty,
            HttpProxyListenPort = Range(settings.Network.HttpProxyListenPort, 0, 65535, 0, "Network.HttpProxyListenPort", corrections)
        };
        var videoContent = settings.Video.Content with
        {
            PreferredSubtitleLanguage = EnumValue(
                settings.Video.Content.PreferredSubtitleLanguage,
                SubtitleLanguagePreference.Unspecified,
                "Video.Content.PreferredSubtitleLanguage",
                corrections),
            PreferredSubtitleType = EnumValue(
                settings.Video.Content.PreferredSubtitleType,
                SubtitleTypePreference.Standard,
                "Video.Content.PreferredSubtitleType",
                corrections)
        };
        var video = settings.Video with
        {
            VideoCodecs = settings.Video.VideoCodecs < 0 ? Corrected("Video.VideoCodecs", 7, corrections) : settings.Video.VideoCodecs,
            Quality = settings.Video.Quality <= 0 ? Corrected("Video.Quality", 120, corrections) : settings.Video.Quality,
            AudioQuality = settings.Video.AudioQuality <= 0 ? Corrected("Video.AudioQuality", 30280, corrections) : settings.Video.AudioQuality,
            IsTranscodingFlvToMp4 = AllowValue(settings.Video.IsTranscodingFlvToMp4, AllowStatus.Yes, "Video.IsTranscodingFlvToMp4", corrections),
            IsTranscodingAacToMp3 = AllowValue(settings.Video.IsTranscodingAacToMp3, AllowStatus.Yes, "Video.IsTranscodingAacToMp3", corrections),
            FfmpegHardwareAcceleration = EnumValueExcept(
                settings.Video.FfmpegHardwareAcceleration,
                FfmpegHardwareAcceleration.NotSet,
                FfmpegHardwareAcceleration.Auto,
                "Video.FfmpegHardwareAcceleration",
                corrections),
            FfmpegMaxParallelJobs = Range(settings.Video.FfmpegMaxParallelJobs, 1, 4, 1, "Video.FfmpegMaxParallelJobs", corrections),
            FileNameParts = settings.Video.FileNameParts.IsDefaultOrEmpty
                ? Corrected("Video.FileNameParts", ApplicationSettingsDefaults.FileNameParts, corrections)
                : settings.Video.FileNameParts,
            FileNamePartTimeFormat = TextValue(settings.Video.FileNamePartTimeFormat, "yyyy-MM-dd", "Video.FileNamePartTimeFormat", corrections),
            OrderFormat = EnumValueExcept(
                settings.Video.OrderFormat,
                OrderFormat.NotSet,
                OrderFormat.Natural,
                "Video.OrderFormat",
                corrections),
            Content = videoContent
        };
        var danmaku = settings.Danmaku with
        {
            OutputFormat = EnumValue(settings.Danmaku.OutputFormat, DanmakuOutputFormat.Ass, "Danmaku.OutputFormat", corrections),
            TopFilter = AllowValue(settings.Danmaku.TopFilter, AllowStatus.No, "Danmaku.TopFilter", corrections),
            BottomFilter = AllowValue(settings.Danmaku.BottomFilter, AllowStatus.No, "Danmaku.BottomFilter", corrections),
            ScrollFilter = AllowValue(settings.Danmaku.ScrollFilter, AllowStatus.No, "Danmaku.ScrollFilter", corrections),
            RemoveEmojiAndSpecialCharacters = AllowValue(
                settings.Danmaku.RemoveEmojiAndSpecialCharacters,
                AllowStatus.No,
                "Danmaku.RemoveEmojiAndSpecialCharacters",
                corrections),
            BlockedKeywords = NormalizeTextList(
                settings.Danmaku.BlockedKeywords,
                "Danmaku.BlockedKeywords",
                corrections),
            BlockedSenderUids = NormalizePositiveIds(
                settings.Danmaku.BlockedSenderUids,
                "Danmaku.BlockedSenderUids",
                corrections),
            IsCustomResolution = AllowValue(settings.Danmaku.IsCustomResolution, AllowStatus.No, "Danmaku.IsCustomResolution", corrections),
            ScreenWidth = Range(settings.Danmaku.ScreenWidth, 1, 16384, 1920, "Danmaku.ScreenWidth", corrections),
            ScreenHeight = Range(settings.Danmaku.ScreenHeight, 1, 16384, 1080, "Danmaku.ScreenHeight", corrections),
            FontName = TextValue(settings.Danmaku.FontName, "黑体", "Danmaku.FontName", corrections),
            FontSize = Range(settings.Danmaku.FontSize, 1, 500, 50, "Danmaku.FontSize", corrections),
            LineCount = Range(settings.Danmaku.LineCount, 0, 1000, 0, "Danmaku.LineCount", corrections),
            LayoutAlgorithm = EnumValueExcept(
                settings.Danmaku.LayoutAlgorithm,
                DanmakuLayoutAlgorithm.None,
                DanmakuLayoutAlgorithm.Sync,
                "Danmaku.LayoutAlgorithm",
                corrections)
        };
        var about = settings.About with
        {
            IsReceiveBetaVersion = AllowValue(settings.About.IsReceiveBetaVersion, AllowStatus.No, "About.IsReceiveBetaVersion", corrections),
            AutoUpdateWhenLaunch = AllowValue(settings.About.AutoUpdateWhenLaunch, AllowStatus.No, "About.AutoUpdateWhenLaunch", corrections),
            SkipVersionOnLaunch = SemanticVersionPolicy.TryNormalizeIdentity(
                settings.About.SkipVersionOnLaunch,
                out var normalizedSkipVersion)
                ? normalizedSkipVersion
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

    private static T EnumValueExcept<T>(
        T value,
        T unsetValue,
        T fallback,
        string field,
        ImmutableArray<string>.Builder corrections)
        where T : struct, Enum
    {
        return Enum.IsDefined(value) && !EqualityComparer<T>.Default.Equals(value, unsetValue)
            ? value
            : Corrected(field, fallback, corrections);
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

    private static ImmutableArray<string> NormalizeTextList(
        ImmutableArray<string> values,
        string field,
        ImmutableArray<string>.Builder corrections)
    {
        var source = values.IsDefault ? ImmutableArray<string>.Empty : values;
        var normalized = source
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        return source.SequenceEqual(normalized, StringComparer.Ordinal)
            ? source
            : Corrected(field, normalized, corrections);
    }

    private static ImmutableArray<long> NormalizePositiveIds(
        ImmutableArray<long> values,
        string field,
        ImmutableArray<string>.Builder corrections)
    {
        var source = values.IsDefault ? ImmutableArray<long>.Empty : values;
        var normalized = source.Where(value => value > 0).Distinct().ToImmutableArray();
        return source.SequenceEqual(normalized)
            ? source
            : Corrected(field, normalized, corrections);
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
