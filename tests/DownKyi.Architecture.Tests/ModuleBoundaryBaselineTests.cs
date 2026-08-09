namespace DownKyi.Architecture.Tests;

public sealed class ModuleBoundaryBaselineTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static readonly Dictionary<string, HashSet<string>> KnownDuplicateSimpleNames =
        new(StringComparer.Ordinal)
        {
            ["BangumiType"] =
            [
                "DownKyi.Core.BiliApi.Bangumi.BangumiType",
                "DownKyi.Core.BiliApi.Users.Models.BangumiType"
            ],
            ["FavoritesMedia"] =
            [
                "DownKyi.Core.BiliApi.Favorites.Models.FavoritesMedia",
                "DownKyi.Presentation.FavoritesMedia"
            ],
            ["Subtitle"] =
            [
                "DownKyi.Core.BiliApi.Models.Json.Subtitle",
                "DownKyi.Core.BiliApi.Video.Models.Subtitle",
                "DownKyi.Core.BiliApi.VideoStream.Models.Subtitle",
                "DownKyi.Core.Danmaku2Ass.Subtitle"
            ],
            ["VideoPage"] =
            [
                "DownKyi.Core.BiliApi.Video.Models.VideoPage",
                "DownKyi.Presentation.VideoPage"
            ]
        };

    private static readonly HashSet<string> KnownGenericTypeNames = new(StringComparer.Ordinal);

    private static readonly HashSet<string> KnownFileTypeMismatches = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, int> KnownOversizedFiles = new(StringComparer.Ordinal);

    private static readonly HashSet<string> KnownPresentationBoundContracts = new(StringComparer.Ordinal)
    {
        "src/DownKyi.Desktop/Services/Download/DownloadManagerCoordinator.cs",
        "src/DownKyi.Desktop/Services/Migration/LegacyUpgradeCoordinator.cs"
    };

    [Fact]
    public void CoreHasNoUiOrQrRenderingDependencies()
    {
        var coreRoot = Path.Combine(RepositoryRoot, "DownKyi.Core");
        var actual = Directory
            .EnumerateFiles(coreRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                if (string.Equals(extension, ".axaml", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var source = File.ReadAllText(path);
                return source.Contains("Avalonia", StringComparison.Ordinal) ||
                       source.Contains("QRCoder", StringComparison.Ordinal);
            })
            .Select(Relative)
            .ToArray();

        Assert.Empty(actual);
    }

    [Fact]
    public void CanonicalResourceAndMediaRuntimeNamesRemainStable()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var desktopDirectories = Directory
            .EnumerateDirectories(desktopRoot)
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Contains("Languages", desktopDirectories);
        Assert.DoesNotContain("Languanges", desktopDirectories);

        var appSource = File.ReadAllText(Path.Combine(desktopRoot, "App.axaml"));
        Assert.Contains("/Languages/Default.axaml", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/Languanges/", appSource, StringComparison.Ordinal);

        var coreDirectories = Directory
            .EnumerateDirectories(Path.Combine(RepositoryRoot, "DownKyi.Core"))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Contains("FFmpeg", coreDirectories);
        Assert.DoesNotContain("FFMpeg", coreDirectories);

        var ffmpegSources = Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot, "DownKyi.Core", "FFmpeg"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        Assert.All(ffmpegSources, source =>
            Assert.DoesNotContain("DownKyi.Core.FFMpeg", source, StringComparison.Ordinal));
    }

    [Fact]
    public void PresentationBoundServiceContractsCannotGrowBeyondTheKnownBaseline()
    {
        var servicesRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services");
        var actual = Directory
            .EnumerateFiles(servicesRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path => CSharpSourceInspector.DeclaresInterfaceWithNamespaceDependency(
                File.ReadAllText(path),
                "DownKyi.ViewModels"))
            .Select(Relative)
            .ToArray();

        AssertSubset(actual, KnownPresentationBoundContracts, "presentation-bound service contract");
    }

    [Fact]
    public void DuplicateSimpleNamesCannotGrowBeyondTheKnownBaseline()
    {
        var declarations = ReadTypeDeclarations();
        var duplicateGroups = declarations
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => new
            {
                group.Key,
                FullNames = group
                    .Select(item => item.FullName)
                    .Distinct(StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal)
            })
            .Where(group => group.FullNames.Count > 1)
            .ToArray();
        var violations = new List<string>();

        foreach (var duplicate in duplicateGroups)
        {
            if (!KnownDuplicateSimpleNames.TryGetValue(duplicate.Key, out var knownNames))
            {
                violations.Add($"new duplicate simple name: {duplicate.Key}");
                continue;
            }

            violations.AddRange(duplicate.FullNames
                .Where(fullName => !knownNames.Contains(fullName))
                .Select(fullName => $"{duplicate.Key} gained {fullName}"));
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void GenericTypeNamesCannotGrowBeyondTheKnownBaseline()
    {
        var genericNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Constant",
            "StorageManager",
            "Utils"
        };
        var actual = ReadTypeDeclarations()
            .Where(item => genericNames.Contains(item.Name))
            .Select(item => $"{item.Path} -> {item.Name}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        AssertSubset(actual, KnownGenericTypeNames, "generic type name");
    }

    [Fact]
    public void FileAndPrimaryTypeMismatchesCannotGrowBeyondTheKnownBaseline()
    {
        var actual = EnumerateProductionFiles("*.cs")
            .Where(path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var declaredNames = ReadDeclaredTypeNames(File.ReadAllText(path));
                if (declaredNames.Length == 0)
                {
                    return false;
                }

                var fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = fileName[..^".axaml".Length];
                }

                return !declaredNames.Any(typeName =>
                    string.Equals(fileName, typeName, StringComparison.Ordinal) ||
                    fileName.StartsWith($"{typeName}.", StringComparison.Ordinal));
            })
            .Select(Relative)
            .ToArray();

        AssertSubset(actual, KnownFileTypeMismatches, "file/type mismatch");
    }

    [Fact]
    public void RoslynDeclarationScanCannotBeBypassedByModifiersOrMalformedInput()
    {
        var source = $$"""
            namespace Adversarial;

            {{new string('/', 100_000)}}
            public readonly record struct StableValue;
            public ref struct StackBuffer { }
            file record HiddenOwner;
            internal interface Contract { }
            """;

        var declarations = ReadDeclaredTypeNames(source);

        Assert.Equal(
            ["StableValue", "StackBuffer", "HiddenOwner", "Contract"],
            declarations);
    }

    [Fact]
    public void ServiceContractScanCannotBeBypassedByFileName()
    {
        const string source = """
            using DownKyi.ViewModels;
            namespace DownKyi.Services;
            internal interface DownloadContract
            {
                ViewModelBase Create();
            }
            """;

        Assert.True(CSharpSourceInspector.DeclaresInterfaceWithNamespaceDependency(
            source,
            "DownKyi.ViewModels"));
    }

    [Fact]
    public void SynchronousHttpScanCannotBeBypassedByReceiverName()
    {
        const string source = """
            using System.IO;
            using System.Net.Http;
            using System.Threading;
            internal sealed class Probe
            {
                private static string Read(HttpClient transport, StreamReader payload, WaitHandle signal)
                {
                    using var response = transport.Send(new HttpRequestMessage());
                    signal.WaitOne();
                    return payload.ReadToEnd();
                }
            }
            """;

        Assert.Equal(
            ["HttpClient.Send", "StreamReader.ReadToEnd", "WaitHandle.WaitOne"],
            CSharpSourceInspector.FindSynchronousRuntimeOperations(source));
    }

    [Fact]
    public void StaticHttpClientScanCannotBeBypassedByNullableTypeOrFieldName()
    {
        const string source = """
            internal sealed class BilibiliHttpClient;
            internal sealed class Probe
            {
                private static BilibiliHttpClient? transportRuntime;
            }
            """;

        Assert.Equal(
            ["static BilibiliHttpClient field"],
            CSharpSourceInspector.FindSynchronousRuntimeOperations(source));
    }

    [Fact]
    public void ProductionInventoryIncludesRootLevelSourcesButExcludesTestTrees()
    {
        var root = Path.Combine(Path.GetTempPath(), $"downkyi-architecture-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "tests"));
            File.WriteAllText(Path.Combine(root, "RootOwner.cs"), "internal class RootOwner { }");
            File.WriteAllText(Path.Combine(root, "src", "SourceOwner.cs"), "internal class SourceOwner { }");
            File.WriteAllText(Path.Combine(root, "tests", "TestOnly.cs"), "internal class TestOnly { }");

            var actual = EnumerateProductionFiles(root, "*.cs")
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(["RootOwner.cs", "src/SourceOwner.cs"], actual);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OversizedProductionFilesCannotGrowBeyondTheKnownBaseline()
    {
        const int lineThreshold = 500;
        var oversized = EnumerateProductionFiles("*.cs")
            .Concat(EnumerateProductionFiles("*.axaml"))
            .Select(path => new { Path = Relative(path), Lines = File.ReadAllLines(path).Length })
            .Where(item => item.Lines > lineThreshold)
            .ToArray();
        var violations = oversized
            .Where(item => !KnownOversizedFiles.TryGetValue(item.Path, out var maximum) || item.Lines > maximum)
            .Select(item => KnownOversizedFiles.TryGetValue(item.Path, out var maximum)
                ? $"{item.Path}: {item.Lines} lines exceeds baseline {maximum}"
                : $"{item.Path}: new oversized file with {item.Lines} lines")
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DomainRestoreIsRestrictedToPersistenceAndMigrationAdapters()
    {
        var actual = EnumerateProductionFiles("*.cs")
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("DownloadTask.Restore", StringComparison.Ordinal) ||
                       source.Contains("DomainDownloadTask.Restore", StringComparison.Ordinal);
            })
            .Select(Relative)
            .ToArray();

        Assert.Equal(
            [
                "src/DownKyi.Desktop/Services/Migration/LegacyDownloadTaskMapper.cs",
                "src/DownKyi.Infrastructure/Downloads/DownloadTaskRecordMapper.cs"
            ],
            actual.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SqliteDownloadStoreCoordinatesDedicatedRecordAndCommandOwners()
    {
        var downloadRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Infrastructure", "Downloads");
        var storeSource = File.ReadAllText(Path.Combine(downloadRoot, "SqliteDownloadTaskStore.cs"));
        var mapperSource = File.ReadAllText(Path.Combine(downloadRoot, "DownloadTaskRecordMapper.cs"));
        var readerSource = File.ReadAllText(Path.Combine(downloadRoot, "DownloadTaskSqlReader.cs"));
        var writerSource = File.ReadAllText(Path.Combine(downloadRoot, "DownloadTaskSqlWriter.cs"));

        Assert.Contains("DownloadTaskRecordMapper.Read", storeSource, StringComparison.Ordinal);
        Assert.Contains("DownloadTaskSqlReader.ReadManyAsync", storeSource, StringComparison.Ordinal);
        Assert.Contains("DownloadTaskSqlWriter", storeSource, StringComparison.Ordinal);
        Assert.Contains("WriteStateRowAsync", storeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadTask.Restore", storeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO downloading", storeSource, StringComparison.Ordinal);
        Assert.Contains("DownloadTask.Restore", mapperSource, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO download_quarantine", readerSource, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO downloading", writerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsNetworkAndAriaOwnersRemainSeparated()
    {
        var settingsRoot = Path.Combine(RepositoryRoot, "DownKyi.Core", "Settings");
        var networkSource = File.ReadAllText(Path.Combine(
            settingsRoot,
            "SettingsManager.Network.cs"));
        var ariaSource = File.ReadAllText(Path.Combine(
            settingsRoot,
            "SettingsManager.Aria.cs"));

        Assert.Contains("public partial class SettingsManager", networkSource, StringComparison.Ordinal);
        Assert.Contains("public partial class SettingsManager", ariaSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAriaToken", networkSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAriaSplit", networkSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AriaConfigLogLevel", networkSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_ariaHttpProxy", networkSource, StringComparison.Ordinal);
        Assert.Contains("GetAriaToken", ariaSource, StringComparison.Ordinal);
        Assert.Contains("GetAriaSplit", ariaSource, StringComparison.Ordinal);
        Assert.Contains("GetAriaFileAllocation", ariaSource, StringComparison.Ordinal);
        Assert.Contains("GetAriaHttpProxy", ariaSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadRuntimeDoesNotPollUiCollectionsForWork()
    {
        var downloadRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
        var actual = Directory
            .EnumerateFiles(downloadRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("_downloadLists.Downloading", StringComparison.Ordinal) &&
                       source.Contains("Task.Delay", StringComparison.Ordinal);
            })
            .Select(Relative)
            .ToArray();

        Assert.Empty(actual);
    }

    [Fact]
    public void BilibiliHttpRuntimeCannotRestoreStaticOrSynchronousTransport()
    {
        var apiRoots = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi"),
            Path.Combine(RepositoryRoot, "src", "DownKyi.Infrastructure", "Bilibili")
        };
        var actual = apiRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .SelectMany(path => CSharpSourceInspector
                .FindSynchronousRuntimeOperations(File.ReadAllText(path))
                .Select(operation => $"{Relative(path)} -> {operation}"))
            .ToArray();

        Assert.Empty(actual);
    }

    [Fact]
    public void DownloadListsExposeOnlyReadOnlyObservableCollections()
    {
        var customCollectionReferences = EnumerateProductionFiles("*.cs")
            .Where(path => File.ReadAllText(path).Contains("ImmutableObservableCollection", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();

        Assert.Empty(customCollectionReferences);
        Assert.False(File.Exists(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels",
            "ImmutableObservableCollection.cs")));

        var stateSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Services", "Download",
            "DownloadListState.cs"));
        Assert.Contains(
            "ReadOnlyObservableCollection<DownloadingItem> Downloading",
            stateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadOnlyObservableCollection<DownloadedItem> Downloaded",
            stateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly RangeObservableCollection<DownloadingItem> _downloading",
            stateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly RangeObservableCollection<DownloadedItem> _downloaded",
            stateSource,
            StringComparison.Ordinal);
    }

    private static TypeDeclaration[] ReadTypeDeclarations()
    {
        return EnumerateProductionFiles("*.cs")
            .SelectMany(path =>
            {
                return CSharpSourceInspector
                    .ReadTypeDeclarations(File.ReadAllText(path))
                    .Select(item => new TypeDeclaration(item.Name, item.FullName, Relative(path)))
                    .ToArray();
            })
            .ToArray();
    }

    private static string[] ReadDeclaredTypeNames(string source)
    {
        return CSharpSourceInspector
            .ReadTypeDeclarations(source)
            .Select(item => item.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateProductionFiles(string pattern)
    {
        return EnumerateProductionFiles(RepositoryRoot, pattern);
    }

    private static IEnumerable<string> EnumerateProductionFiles(string repositoryRoot, string pattern)
    {
        var rootFiles = Directory.EnumerateFiles(repositoryRoot, pattern, SearchOption.TopDirectoryOnly);
        var sourceFiles = new[]
            {
                Path.Combine(repositoryRoot, "DownKyi"),
                Path.Combine(repositoryRoot, "DownKyi.Core"),
                Path.Combine(repositoryRoot, "src")
            }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(repositoryRoot, path));
        return rootFiles.Concat(sourceFiles).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertSubset(
        IEnumerable<string> actual,
        HashSet<string> knownBaseline,
        string description)
    {
        var unexpected = actual
            .Distinct(StringComparer.Ordinal)
            .Where(item => !knownBaseline.Contains(item))
            .Order(StringComparer.Ordinal)
            .Select(item => $"New {description}: {item}")
            .ToArray();

        Assert.True(unexpected.Length == 0, string.Join(Environment.NewLine, unexpected));
    }

    private static bool IsBuildOutput(string path)
    {
        return IsBuildOutput(RepositoryRoot, path);
    }

    private static bool IsBuildOutput(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase) ||
               segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string Relative(string path)
    {
        return Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }

    private sealed record TypeDeclaration(string Name, string FullName, string Path);
}
