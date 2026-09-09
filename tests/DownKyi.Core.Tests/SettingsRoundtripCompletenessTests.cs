using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.FileName;
using DownKyi.Core.Settings;

namespace DownKyi.Core.Tests;

public sealed class SettingsRoundtripCompletenessTests
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    [Fact]
    public async Task EveryApplicationSettingsLeafSurvivesCreateApplyAndPersistenceRoundtrip()
    {
        var baselineDirectory = CreateTestDirectory();
        ApplicationSettings baseline;
        try
        {
            using var store = new SettingsStore(Path.Combine(baselineDirectory, "settings.json"));
            baseline = store.Current;
        }
        finally
        {
            DeleteDirectory(baselineDirectory);
        }

        var leaves = DiscoverLeaves(typeof(ApplicationSettings));
        Assert.NotEmpty(leaves);

        foreach (var leaf in leaves)
        {
            var directory = CreateTestDirectory();
            var settingsPath = Path.Combine(directory, "settings.json");
            try
            {
                var candidates = CreateCandidates(baseline, leaf, leaves);

                string? futureSettings = null;
                if (leaf.Path == nameof(ApplicationSettings.SchemaVersion))
                {
                    futureSettings = JsonSerializer.Serialize(
                        baseline with
                        {
                            SchemaVersion = ApplicationSettingsValidator.CurrentSchemaVersion + 1,
                            Video = baseline.Video with { FfmpegMaxParallelJobs = 2 }
                        },
                        SnapshotJsonOptions);
                    await File.WriteAllTextAsync(
                        settingsPath,
                        futureSettings,
                        TestContext.Current.CancellationToken);
                }

                using var manager = new SettingsManager(settingsPath);
                if (leaf.Path == nameof(ApplicationSettings.SchemaVersion))
                {
                    Assert.Equal(
                        ApplicationSettingsValidator.CurrentSchemaVersion + 1,
                        manager.CreateSnapshot(persistDefaultValues: false).SchemaVersion);
                }

                using (var store = new SettingsStore(manager))
                {
                    if (leaf.Path == nameof(ApplicationSettings.SchemaVersion))
                    {
                        Assert.Equal(
                            ApplicationSettingsValidator.CurrentSchemaVersion,
                            manager.CreateSnapshot(persistDefaultValues: false).SchemaVersion);
                    }

                    foreach (var candidate in candidates)
                    {
                        var validation = ApplicationSettingsValidator.Validate(candidate);
                        Assert.True(
                            validation.Corrections.IsEmpty,
                            $"The legal roundtrip value for '{leaf.Path}' was rejected: " +
                            string.Join(", ", validation.Corrections));

                        store.Update(_ => candidate);
                        AssertSnapshotsEqual(candidate, manager.CreateSnapshot(), $"Apply/Create for {leaf.Path}");
                        await store.FlushAsync(TestContext.Current.CancellationToken);

                        if (!IsValidatorDerived(leaf))
                        {
                            using var reopened = new SettingsStore(settingsPath);
                            AssertSnapshotsEqual(candidate, reopened.Current, $"Persistence/Create for {leaf.Path}");
                        }
                    }
                }

                if (leaf.Path == nameof(ApplicationSettings.SchemaVersion))
                {
                    // A future schema is retained on disk, while SettingsStore validates and applies
                    // the supported schema to SettingsManager's live snapshot.
                    Assert.Equal(
                        futureSettings,
                        await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }
    }

    private static ApplicationSettings[] CreateCandidates(
        ApplicationSettings baseline,
        SettingsLeaf leaf,
        IReadOnlyList<SettingsLeaf> leaves)
    {
        var currentValue = leaf.Read(baseline);
        var replacement = CreateLegalAlternative(leaf.Path, leaf.ValueType, currentValue);
        if (!IsValidatorDerived(leaf))
        {
            Assert.False(
                LeafValuesEqual(currentValue, replacement),
                $"Roundtrip candidate for '{leaf.Path}' did not differ from its baseline.");
        }

        var first = CreateCandidate(baseline, leaf, replacement);
        var candidates = IsValidatorDerived(leaf)
            ? new[] { first }
            : new[] { first, CreateCandidate(first, leaf, currentValue!) };
        if (!IsValidatorDerived(leaf))
        {
            Assert.False(
                LeafValuesEqual(leaf.Read(first), leaf.Read(candidates[1])),
                $"Second roundtrip candidate for '{leaf.Path}' did not differ from its baseline.");
        }

        foreach (var sameTypeLeaf in leaves.Where(candidate =>
                     !IsValidatorDerived(leaf)
                     && candidate.Path != leaf.Path
                     && candidate.ValueType == leaf.ValueType))
        {
            Assert.True(
                candidates.Any(candidate => !LeafValuesEqual(
                    leaf.Read(candidate),
                    sameTypeLeaf.Read(candidate))),
                $"'{leaf.Path}' is indistinguishable from same-type source '{sameTypeLeaf.Path}'.");
        }

        return candidates;
    }

    private static ApplicationSettings CreateCandidate(
        ApplicationSettings baseline,
        SettingsLeaf leaf,
        object replacement)
    {
        var root = JsonSerializer.SerializeToNode(baseline, SnapshotJsonOptions)
            ?? throw new InvalidOperationException("ApplicationSettings could not be serialized.");
        SetJsonValue(root, leaf.Path, leaf.ValueType, replacement);

        var candidate = root.Deserialize<ApplicationSettings>(SnapshotJsonOptions)
            ?? throw new InvalidOperationException($"Candidate for '{leaf.Path}' could not be deserialized.");
        if (candidate.Network.IsAriaHttpProxy == AllowStatus.Yes)
        {
            // Enabling this capability is validator-dependent on a complete local endpoint.
            SetJsonValue(root, "Network.AriaHttpProxy", typeof(string), "127.0.0.1");
            SetJsonValue(root, "Network.AriaHttpProxyListenPort", typeof(int), 17890);
        }

        if (leaf.Path == "Network.CustomNetworkProxy"
            && !string.IsNullOrEmpty((string)replacement))
        {
            // The custom address is intentionally cleared unless the custom mode owns it.
            SetJsonValue(root, "Network.NetworkProxy", typeof(NetworkProxy), NetworkProxy.Custom);
        }

        candidate = root.Deserialize<ApplicationSettings>(SnapshotJsonOptions)
            ?? throw new InvalidOperationException($"Candidate for '{leaf.Path}' could not be deserialized.");
        Assert.True(
            LeafValuesEqual(replacement, leaf.Read(candidate)),
            $"Serialized candidate for '{leaf.Path}' did not retain replacement '{FormatValue(replacement)}'.");
        return candidate;
    }

    private static bool IsValidatorDerived(SettingsLeaf leaf) =>
        leaf.Path == nameof(ApplicationSettings.SchemaVersion);

    private static object CreateLegalAlternative(string path, Type valueType, object? currentValue)
    {
        if (path == nameof(ApplicationSettings.SchemaVersion))
        {
            return ApplicationSettingsValidator.CurrentSchemaVersion;
        }

        if (valueType == typeof(bool))
        {
            return !(bool)currentValue!;
        }

        if (valueType == typeof(long) && path == "User.Mid")
        {
            return 4242L;
        }

        if (valueType == typeof(string))
        {
            return path switch
            {
                "Network.UserAgent" => "DownKyi-Roundtrip-Guard/1.0",
                "Network.CustomNetworkProxy" => "http://127.0.0.1:18080",
                "Network.HttpProxy" => "127.0.0.1",
                "Network.AriaToken" => "roundtrip-token",
                "Network.AriaHost" => "https://aria.example.test",
                "Network.AriaHttpProxy" => "127.0.0.1",
                "Video.SaveVideoRootPath" => "C:/DownKyi/Roundtrip",
                "Video.FileNamePartTimeFormat" => "yyyyMMdd-HHmmss",
                "Danmaku.FontName" => "Roundtrip Sans",
                "About.SkipVersionOnLaunch" => "9.8.7",
                "User.Name" => "roundtrip-user",
                "User.ImgKey" => "roundtrip-img",
                "User.SubKey" => "roundtrip-sub",
                _ => throw UnsupportedLeaf(path, valueType)
            };
        }

        if (valueType == typeof(int))
        {
            return path switch
            {
                "Network.MaxCurrentDownloads" => 4,
                "Network.Split" => 9,
                "Network.HttpProxyListenPort" => 18081,
                "Network.AriaListenPort" => 35077,
                "Network.AriaSplit" => 6,
                "Network.AriaMaxConnectionPerServer" => 9,
                "Network.AriaMinSplitSize" => 11,
                "Network.AriaMaxOverallDownloadLimit" => 17,
                "Network.AriaMaxDownloadLimit" => 23,
                "Network.AriaHttpProxyListenPort" => 17890,
                "Video.VideoCodecs" => 12,
                "Video.Quality" => 80,
                "Video.AudioQuality" => 30232,
                "Video.VideoParseType" => 1,
                "Video.FfmpegMaxParallelJobs" => 2,
                "Danmaku.ScreenWidth" => 1280,
                "Danmaku.ScreenHeight" => 720,
                "Danmaku.FontSize" => 42,
                "Danmaku.LineCount" => 12,
                _ => throw UnsupportedLeaf(path, valueType)
            };
        }

        if (valueType == typeof(double))
        {
            return path switch
            {
                "Window.Width" => 1280d,
                "Window.Height" => 800d,
                "Window.X" => 100d,
                "Window.Y" => 200d,
                _ => throw UnsupportedLeaf(path, valueType)
            };
        }

        if (valueType == typeof(AllowStatus))
        {
            return Equals(currentValue, AllowStatus.Yes) ? AllowStatus.No : AllowStatus.Yes;
        }

        if (valueType.IsEnum)
        {
            return path switch
            {
                "Basic.ThemeMode" => ThemeMode.Dark,
                "Basic.AfterDownload" => AfterDownloadOperation.OpenFolder,
                "Basic.ParseScope" => ParseScope.CurrentSection,
                "Basic.DownloadFinishedSort" => DownloadFinishedSort.DownloadDesc,
                "Basic.RepeatDownloadStrategy" => RepeatDownloadStrategy.ReDownload,
                "Network.Downloader" => Downloader.BuiltIn,
                "Network.NetworkProxy" => NetworkProxy.System,
                "Network.AriaLogLevel" => AriaConfigLogLevel.INFO,
                "Network.AriaFileAllocation" => AriaConfigFileAllocation.PREALLOC,
                "Video.FfmpegHardwareAcceleration" => FfmpegHardwareAcceleration.Disabled,
                "Video.OrderFormat" => OrderFormat.LeadingZeros,
                "Danmaku.LayoutAlgorithm" => DanmakuLayoutAlgorithm.Async,
                _ => throw UnsupportedLeaf(path, valueType)
            };
        }

        if (valueType == typeof(ImmutableArray<string>))
        {
            return ImmutableArray.Create("C:/DownKyi/Previous", "D:/DownKyi/Previous");
        }

        if (valueType == typeof(ImmutableArray<FileNamePart>))
        {
            return ImmutableArray.Create(FileNamePart.MainTitle, FileNamePart.Hyphen, FileNamePart.Bvid);
        }

        throw UnsupportedLeaf(path, valueType);
    }

    private static List<SettingsLeaf> DiscoverLeaves(Type rootType)
    {
        var leaves = new List<SettingsLeaf>();
        DiscoverLeaves(rootType, [], string.Empty, leaves);
        return leaves;
    }

    private static void DiscoverLeaves(
        Type nodeType,
        IReadOnlyList<PropertyInfo> ancestors,
        string parentPath,
        ICollection<SettingsLeaf> leaves)
    {
        foreach (var property in nodeType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var path = string.IsNullOrEmpty(parentPath)
                ? property.Name
                : $"{parentPath}.{property.Name}";
            var properties = ancestors.Append(property).ToArray();
            if (IsSupportedLeafType(property.PropertyType))
            {
                leaves.Add(new SettingsLeaf(path, property.PropertyType, properties));
                continue;
            }

            if (property.PropertyType.Name.EndsWith("ApplicationSettings", StringComparison.Ordinal))
            {
                DiscoverLeaves(property.PropertyType, properties, path, leaves);
                continue;
            }

            throw UnsupportedLeaf(path, property.PropertyType);
        }
    }

    private static bool IsSupportedLeafType(Type type)
    {
        if (type is { IsEnum: true } || type == typeof(bool) || type == typeof(int)
            || type == typeof(long) || type == typeof(double) || type == typeof(string))
        {
            return true;
        }

        return type == typeof(ImmutableArray<string>)
               || type == typeof(ImmutableArray<FileNamePart>);
    }

    private static void SetJsonValue(JsonNode root, string path, Type valueType, object value)
    {
        var segments = path.Split('.');
        var node = root;
        foreach (var segment in segments[..^1])
        {
            node = node[segment]
                ?? throw new InvalidOperationException($"Settings node '{segment}' is missing for '{path}'.");
        }

        node[segments[^1]] = JsonSerializer.SerializeToNode(value, valueType, SnapshotJsonOptions);
    }

    private static void AssertSnapshotsEqual(
        ApplicationSettings expected,
        ApplicationSettings actual,
        string context)
    {
        foreach (var leaf in DiscoverLeaves(typeof(ApplicationSettings)))
        {
            var expectedValue = leaf.Read(expected);
            var actualValue = leaf.Read(actual);
            Assert.True(
                LeafValuesEqual(expectedValue, actualValue),
                $"{context}: '{leaf.Path}' expected '{FormatValue(expectedValue)}' " +
                $"but was '{FormatValue(actualValue)}'.");
        }
    }

    private static bool LeafValuesEqual(object? expected, object? actual)
    {
        if (expected is double expectedDouble && actual is double actualDouble)
        {
            return expectedDouble.Equals(actualDouble)
                   || double.IsNaN(expectedDouble) && double.IsNaN(actualDouble);
        }

        if (expected is IEnumerable expectedSequence && actual is IEnumerable actualSequence
            && expected is not string && actual is not string)
        {
            return expectedSequence.Cast<object?>().SequenceEqual(actualSequence.Cast<object?>());
        }

        return Equals(expected, actual);
    }

    private static string FormatValue(object? value)
    {
        return value is IEnumerable sequence and not string
            ? $"[{string.Join(", ", sequence.Cast<object?>())}]"
            : value?.ToString() ?? "<null>";
    }

    private static InvalidOperationException UnsupportedLeaf(string path, Type type)
    {
        return new InvalidOperationException(
            $"ApplicationSettings leaf '{path}' has no explicit legal roundtrip value for '{type}'.");
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-settings-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record SettingsLeaf(
        string Path,
        Type ValueType,
        IReadOnlyList<PropertyInfo> Properties)
    {
        public object? Read(ApplicationSettings settings)
        {
            object? value = settings;
            foreach (var property in Properties)
            {
                value = property.GetValue(value);
            }

            return value;
        }
    }
}
