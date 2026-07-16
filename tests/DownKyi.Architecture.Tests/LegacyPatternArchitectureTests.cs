using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class LegacyPatternArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void ProductionSourceCannotRestoreLegacyOrBlockingPatterns()
    {
        var forbiddenPatterns = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App.Current"] = @"\bApp\.Current\b",
            ["Container.Resolve"] = @"\bContainer\.Resolve\b",
            ["ContainerLocator"] = @"\bContainerLocator\b",
            ["Thread.Sleep"] = @"\bThread\.Sleep\s*\(",
            ["Task.Wait"] = @"\.Wait\s*\(",
            ["GetAwaiter.GetResult"] = @"\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(",
            ["new HttpClient"] = @"\bnew\s+HttpClient\s*\(",
            ["Console output"] = @"\bConsole\s*\.",
            ["async void"] = @"\basync\s+void\b"
        };
        var violations = EnumerateProductionSourceFiles()
            .SelectMany(path =>
            {
                var source = File.ReadAllText(path);
                return forbiddenPatterns
                    .Where(pattern => Regex.IsMatch(
                        source,
                        pattern.Value,
                        RegexOptions.CultureInvariant,
                        RegexTimeout))
                    .Select(pattern => $"{Relative(path)} -> {pattern.Key}");
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void NewArchitectureCannotSynchronouslyReadTaskResult()
    {
        var violations = EnumerateSourceFiles(Path.Combine(RepositoryRoot, "src"))
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                @"\.Result\b",
                RegexOptions.CultureInvariant,
                RegexTimeout))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ViewModelsCannotOffloadWorkWithTaskRun()
    {
        var violations = EnumerateSourceFiles(Path.Combine(RepositoryRoot, "DownKyi", "ViewModels"))
            .Where(path => File.ReadAllText(path).Contains("Task.Run", StringComparison.Ordinal))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProductionSourceCannotContainEmptyCatchBlocks()
    {
        const string emptyCatchPattern = @"catch\b[^\{]{0,1000}\{\s*\}";
        var violations = EnumerateProductionSourceFiles()
            .Where(path => Regex.IsMatch(
                RemoveComments(File.ReadAllText(path)),
                emptyCatchPattern,
                RegexOptions.CultureInvariant,
                RegexTimeout))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProductionSourceCannotOwnMutableStaticCollections()
    {
        const string mutableStaticCollectionPattern =
            @"^\s*(?:public|internal|protected|private)?\s*static\s+(?:readonly\s+)?" +
            @"(?:List|Dictionary|HashSet|Collection|ObservableCollection|ConcurrentDictionary|" +
            @"ConcurrentQueue|ConcurrentBag)\s*<[^\r\n;]+>\s+\w+\s*(?:=|;)";
        var violations = EnumerateProductionSourceFiles()
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                mutableStaticCollectionPattern,
                RegexOptions.CultureInvariant | RegexOptions.Multiline,
                RegexTimeout))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void AnalyzerSuppressionsRemainLimitedToDocumentedCompatibilityCases()
    {
        var expectedPragmas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DownKyi.Core/BiliApi/Sign/WbiSign.cs"] = "CA5351",
            ["DownKyi.Core/Utils/Encryptor/LegacySettingsDecryptor.cs"] = "CA5351"
        };
        var sourceFiles = EnumerateProductionSourceFiles().ToArray();
        var pragmaFiles = sourceFiles
            .Where(path => File.ReadAllText(path).Contains("#pragma warning disable", StringComparison.Ordinal))
            .ToDictionary(Relative, File.ReadAllText, StringComparer.Ordinal);

        Assert.Equal(expectedPragmas.Keys.Order(), pragmaFiles.Keys.Order());
        foreach (var expected in expectedPragmas)
        {
            var source = pragmaFiles[expected.Key];
            Assert.True(Regex.Count(
                source,
                $@"#pragma\s+warning\s+disable\s+{expected.Value}\b",
                RegexOptions.CultureInvariant,
                RegexTimeout) == 1);
            Assert.True(Regex.Count(
                source,
                $@"#pragma\s+warning\s+restore\s+{expected.Value}\b",
                RegexOptions.CultureInvariant,
                RegexTimeout) == 1);
        }

        var forbiddenSourceSuppressions = sourceFiles
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("#nullable disable", StringComparison.Ordinal)
                       || source.Contains("SuppressMessage", StringComparison.Ordinal)
                       || string.Equals(Path.GetFileName(path), "GlobalSuppressions.cs", StringComparison.Ordinal);
            })
            .Select(Relative)
            .ToArray();
        Assert.Empty(forbiddenSourceSuppressions);

        var buildPolicyFiles = Directory
            .EnumerateFiles(RepositoryRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path) == ".editorconfig"
                           || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var forbiddenBuildSuppressions = buildPolicyFiles
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("<NoWarn>", StringComparison.Ordinal)
                       || Regex.IsMatch(
                           source,
                           @"severity\s*=\s*(?:none|silent)\b",
                           RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                           RegexTimeout)
                       || source.Contains("<EnableNETAnalyzers>false", StringComparison.OrdinalIgnoreCase);
            })
            .Select(Relative)
            .ToArray();
        Assert.Empty(forbiddenBuildSuppressions);
    }

    [Fact]
    public void PrismAndDryIocCannotReturnToProductionComposition()
    {
        var files = EnumerateProductionSourceFiles()
            .Concat(Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "DownKyi"),
                "*.axaml",
                SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !IsBuildOutput(path)))
            .Concat(Directory.EnumerateFiles(RepositoryRoot, "*.props", SearchOption.TopDirectoryOnly));
        var violations = files
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("Prism", StringComparison.Ordinal)
                       || source.Contains("DryIoc", StringComparison.Ordinal);
            })
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> EnumerateProductionSourceFiles()
    {
        return new[]
            {
                Path.Combine(RepositoryRoot, "DownKyi"),
                Path.Combine(RepositoryRoot, "DownKyi.Core"),
                Path.Combine(RepositoryRoot, "src")
            }
            .SelectMany(EnumerateSourceFiles);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));
    }

    private static bool IsBuildOutput(string path)
    {
        var relative = Path.GetRelativePath(RepositoryRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
               || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string RemoveComments(string source)
    {
        var withoutBlockComments = Regex.Replace(
            source,
            @"/\*[\s\S]*?\*/",
            string.Empty,
            RegexOptions.CultureInvariant,
            RegexTimeout);
        return Regex.Replace(
            withoutBlockComments,
            @"//[^\r\n]*",
            string.Empty,
            RegexOptions.CultureInvariant,
            RegexTimeout);
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
}
