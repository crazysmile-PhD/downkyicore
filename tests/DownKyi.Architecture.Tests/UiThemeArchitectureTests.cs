using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class UiThemeArchitectureTests
{
    private static readonly string[] ExpectedLanguageCatalogFiles =
        ["en-US.axaml", "ja-JP.axaml", "zh-CN.axaml", "zh-TW.axaml"];
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DesktopLanguageCatalogsUseCanonicalLocaleFileNames()
    {
        var languageDirectory = Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Desktop",
            "Languages");
        var actual = Directory
            .EnumerateFiles(languageDirectory, "*.axaml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedLanguageCatalogFiles, actual);
    }

    [Fact]
    public void DesktopLanguagesKeepTheSameKeyOrderAndFormatArguments()
    {
        var baseline = ReadLanguage("zh-CN.axaml");
        foreach (var language in new[] { "zh-TW.axaml", "en-US.axaml", "ja-JP.axaml" })
        {
            var translated = ReadLanguage(language);
            Assert.Equal(baseline.Keys.ToArray(), translated.Keys.ToArray());
            foreach (var (key, value) in baseline)
            {
                Assert.Equal(
                    FormatArguments(value),
                    FormatArguments(translated[key]));
            }
        }
    }

    [Fact]
    public void LiteralDesktopLanguageLookupsUseDeclaredKeys()
    {
        var languageKeys = ReadLanguage("zh-CN.axaml").Keys.ToHashSet(StringComparer.Ordinal);
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var missing = Directory
            .EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "DictionaryResource\\.(?:GetString|Format)\\(\\s*\"(?<key>[^\"]+)\"")
                .Select(match => new
                {
                    Path = Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'),
                    Key = match.Groups["key"].Value
                }))
            .Where(reference => !languageKeys.Contains(reference.Key))
            .Select(reference => $"{reference.Path}: {reference.Key}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, $"Undeclared language lookups: {string.Join(", ", missing)}");
    }

    [Fact]
    public void DesktopXamlResourcesResolveToLocalOrThemeKeys()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var sources = Directory
            .EnumerateFiles(desktopRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .Select(path => new
            {
                Path = Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'),
                Source = File.ReadAllText(path)
            })
            .ToArray();
        var declaredKeys = sources
            .SelectMany(source => Regex.Matches(source.Source, "\\bx:Key=\"(?<key>[^\"]+)\"")
                .Select(match => match.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);
        // These control resources are supplied by Avalonia's FluentTheme.
        var fluentThemeKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "DataGridTextColumnCellTextBlockMargin",
            "VerticalSmallScrollThumbScaleTransform"
        };
        var missing = sources
            .SelectMany(source => Regex.Matches(
                    source.Source,
                    "\\{(?:DynamicResource|StaticResource)\\s+(?<key>[A-Za-z][\\w.-]*)\\}")
                .Select(match => new { source.Path, Key = match.Groups["key"].Value }))
            .Where(reference => !declaredKeys.Contains(reference.Key) && !fluentThemeKeys.Contains(reference.Key))
            .Select(reference => $"{reference.Path}: {reference.Key}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, $"Unresolved AXAML resources: {string.Join(", ", missing)}");
    }

    [Fact]
    public void SimplifiedChineseResourcesDoNotContainTraditionalScriptCharacters()
    {
        const string traditionalCharacters =
            "並佇佈併個備傳僅優儲內兩別刪則剛創劇動務匯區參問啟單嗎嗶嘗國圍圖執報壞夾學實寫寬將專尋對幣庫強彈後徑復態憑憶應掃換損擇擔敗數斷於時暫會條棄業構標機檔檢欄權歷歸決沒測準滾濾瀏為無營狀獲現產畫異當發監確碼種稱筆節範簡紀級結絕統絲綁經綜網緒線編繼續義習聲腦與舊藝處號術衝補裝裡製複見規視覽觀訂計訊記設診詢試該詳誌認語誤課調請證識議讀變讚負責貼資質軌軟載輸轉這連進過違選遺還邊郵錄錯鍵鎖鏈鑰長閉開間閱關際離電韓響頁頂項須預頭頻題類顯餘饋驗驟體麼點";
        var violations = ReadLanguage("zh-CN.axaml")
            .Select(resource => new
            {
                resource.Key,
                Characters = resource.Value
                    .Where(traditionalCharacters.Contains)
                    .Distinct()
                    .Order()
                    .ToArray()
            })
            .Where(resource => resource.Characters.Length > 0)
            .Select(resource => $"{resource.Key} ({new string(resource.Characters)})")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Simplified Chinese resources contain Traditional-only characters: {string.Join(", ", violations)}");
    }

    private static Dictionary<string, string> ReadLanguage(string fileName)
    {
        var document = XDocument.Parse(ReadSource("src", "DownKyi.Desktop", "Languages", fileName));
        var keyNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        return document.Root!.Elements().ToDictionary(
            element => (string)element.Attribute(keyNamespace + "Key")!,
            element => element.Value,
            StringComparer.Ordinal);
    }

    private static string[] FormatArguments(string value)
    {
        return Regex.Matches(value, @"\{\d+(?::[^}]*)?\}")
            .Select(match => match.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void DesktopUsesOneFluentThemeAndCentralDesignTokens()
    {
        var appSource = ReadSource("src", "DownKyi.Desktop", "App.axaml");
        var themeSource = ReadSource("src", "DownKyi.Desktop", "Themes", "ThemeDefault.axaml");
        var tokenSource = ReadSource("src", "DownKyi.Desktop", "Themes", "DesignTokens.axaml");
        var projectSource = ReadSource("src", "DownKyi.Desktop", "DownKyi.Desktop.csproj");

        Assert.Contains("<FluentTheme />", appSource, StringComparison.Ordinal);
        Assert.Contains("Themes/Fluent.xaml", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SimpleTheme", appSource, StringComparison.Ordinal);
        Assert.Contains("/Themes/DesignTokens.axaml", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/Themes/DesignTokens.axaml", themeSource, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Default\"", themeSource, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Light\"", themeSource, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Dark\"", themeSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiFontSizeBody", tokenSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiSpacingMedium", tokenSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiRadiusMedium", tokenSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiElevationLow", tokenSource, StringComparison.Ordinal);
        Assert.Contains("Avalonia.Themes.Fluent", projectSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia.Themes.Simple", projectSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultAndDarkPalettesDeclareTheSameSemanticColorKeys()
    {
        var defaultKeys = ExtractKeys(ReadSource(
                "src", "DownKyi.Desktop", "Themes", "Colors", "ColorDefault.axaml"))
            .Where(static key => key.StartsWith("Color", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var darkKeys = ExtractKeys(ReadSource(
                "src", "DownKyi.Desktop", "Themes", "Colors", "ColorDark.axaml"))
            .Where(static key => key.StartsWith("Color", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(defaultKeys);
        Assert.Equal(defaultKeys, darkKeys);
    }

    [Fact]
    public void DarkReadingPaletteSeparatesSurfacesTextAndInteractionStates()
    {
        var appSource = ReadSource("src", "DownKyi.Desktop", "App.axaml");
        var windowSource = ReadSource("src", "DownKyi.Desktop", "Views", "MainWindow.axaml");
        var darkPalette = ReadSource(
            "src", "DownKyi.Desktop", "Themes", "Colors", "ColorDark.axaml");

        Assert.Contains(
            "Segoe UI Variable Text,Segoe UI,Microsoft JhengHei UI,Microsoft YaHei UI",
            appSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(">Simsun,", appSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Background=\"{DynamicResource SpacePaper}\"",
            windowSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorSurfaceBase\">#FF0F0F0F</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorSurfaceRaised\">#FF212121</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorSurfaceOverlay\">#FF282828</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorTextPrimary\">#FFF1F1F1</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorTextSecondary\">#FFAAAAAA</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorTextDark\">#FFC7C7C7</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<Color x:Key=\"ColorTextDark\">white</Color>",
            darkPalette,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "<Color x:Key=\"ColorSelectionFill\">#33FFFFFF</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorControlStrokeFocus\">#FF1C62B9</Color>",
            darkPalette,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedControlsReserveAccentForFocusLinksAndActiveState()
    {
        var buttonTheme = ReadSource(
            "src", "DownKyi.Desktop", "Themes", "Styles", "StyleBtn.axaml");
        var navigationTheme = ReadSource(
            "src", "DownKyi.Desktop", "Themes", "Styles", "StyleListBox.axaml");
        var indexView = ReadSource("src", "DownKyi.Desktop", "Views", "ViewIndex.axaml");

        Assert.Contains("BrushControlFill", buttonTheme, StringComparison.Ordinal);
        Assert.Contains("BrushControlStroke", buttonTheme, StringComparison.Ordinal);
        Assert.DoesNotContain("BrushPrimaryTranslucent", buttonTheme, StringComparison.Ordinal);
        Assert.Contains("BrushSelectionFill", navigationTheme, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionIndicator", navigationTheme, StringComparison.Ordinal);
        Assert.Contains("SpaceAccent", indexView, StringComparison.Ordinal);
        Assert.Contains("SpaceMuted", indexView, StringComparison.Ordinal);
    }

    [Fact]
    public void ButtonsInheritReadableForegroundFromTheirTheme()
    {
        var viewsRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Views");
        var violations = Directory
            .EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .SelectMany(path => Regex
                .Matches(File.ReadAllText(path), "<Button\\b(?<attributes>[^>]*)>")
                .Where(match => match.Groups["attributes"].Value.Contains(
                    "Foreground=\"{DynamicResource BrushText}\"",
                    StringComparison.Ordinal))
                .Select(_ => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/')))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Buttons must inherit a readable foreground from their theme: {string.Join(", ", violations)}");
    }

    [Fact]
    public void SettingsUseSemanticSurfacesAndReadableTypography()
    {
        var settingsRoot = Path.Combine(
            RepositoryRoot, "src", "DownKyi.Desktop", "Views", "Settings");
        var settingsSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(settingsRoot, "*.axaml").Select(File.ReadAllText));
        var languageSource = ReadSource(
            "src", "DownKyi.Desktop", "Languages", "zh-CN.axaml");

        Assert.DoesNotContain("Background=\"LightGray\"", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FontSize=\"12\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiFontSizeBody", settingsSource, StringComparison.Ordinal);
        Assert.Contains("Classes=\"studio-title\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<system:String x:Key=\"FontName\">弹幕字体：</system:String>", languageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolboxSurfacesUseSemanticThemeResources()
    {
        var toolboxViews = new[]
        {
            ReadSource("src", "DownKyi.Desktop", "Views", "ViewToolbox.axaml"),
            ReadSource("src", "DownKyi.Desktop", "Views", "Toolbox", "ViewBiliHelper.axaml"),
            ReadSource("src", "DownKyi.Desktop", "Views", "Toolbox", "ViewExtractMedia.axaml"),
            ReadSource("src", "DownKyi.Desktop", "Views", "Toolbox", "ViewDelogo.axaml")
        };

        Assert.All(
            toolboxViews,
            source => Assert.Contains("/Themes/Styles/Studio.axaml", source, StringComparison.Ordinal));
        Assert.Contains("{DynamicResource SpacePaper}",
            ReadSource("src", "DownKyi.Desktop", "Themes", "Styles", "Studio.axaml"), StringComparison.Ordinal);

        var combinedSource = string.Join(Environment.NewLine, toolboxViews);
        Assert.DoesNotContain("Background=\"LightGray\"", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"Black\"", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"White\"", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush=\"Gray\"", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#FF1E1E1E\"", combinedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReferencedSemanticThemeResourceIsDeclared()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var declaredKeys = Directory
            .EnumerateFiles(Path.Combine(desktopRoot, "Themes"), "*.axaml", SearchOption.AllDirectories)
            .SelectMany(static path => ExtractKeys(File.ReadAllText(path)))
            .ToHashSet(StringComparer.Ordinal);
        var referencedKeys = Directory
            .EnumerateFiles(desktopRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .SelectMany(static path => ExtractSemanticResourceReferences(File.ReadAllText(path)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingKeys = referencedKeys
            .Where(key => !declaredKeys.Contains(key))
            .ToArray();

        Assert.True(
            missingKeys.Length == 0,
            $"Missing semantic theme resources: {string.Join(", ", missingKeys)}");
    }

    [Fact]
    public void DesktopThemeControllerIsTheOnlyThemeSwitchOwner()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var desktopSources = Directory
            .EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .ToArray();
        var requestedThemeOwners = desktopSources
            .Where(path => File.ReadAllText(path).Contains("RequestedThemeVariant", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var presentationSources = desktopSources
            .Where(path => path.Contains(
                $"{Path.DirectorySeparatorChar}ViewModels{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) ||
                path.Contains(
                    $"{Path.DirectorySeparatorChar}Presentation{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Equal(
            ["src/DownKyi.Desktop/Appearance/DesktopThemeController.cs"],
            requestedThemeOwners);
        Assert.DoesNotContain(
            desktopSources,
            path => File.ReadAllText(path).Contains("ActualThemeVariantChanged", StringComparison.Ordinal));
        Assert.DoesNotContain(
            presentationSources,
            source => source.Contains("DictionaryResource.GetColor", StringComparison.Ordinal));
        Assert.DoesNotContain(
            presentationSources,
            source => source.Contains("Application.Current", StringComparison.Ordinal));
        Assert.DoesNotContain(
            presentationSources,
            source => source.Contains("ActualThemeVariant", StringComparison.Ordinal));
        Assert.DoesNotContain(
            presentationSources,
            source => source.Contains("ResourceDictionary", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsThemeChoicesUseExplicitThemeModes()
    {
        var settingsView = ReadSource(
            "src", "DownKyi.Desktop", "Views", "Settings", "ViewBasic.axaml");
        var resourceLookup = ReadSource(
            "src", "DownKyi.Desktop", "Utils", "DictionaryResource.cs");

        Assert.Contains(
            "CommandParameter=\"{x:Static settings:ThemeMode.Light}\"",
            settingsView,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"{x:Static settings:ThemeMode.Dark}\"",
            settingsView,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"{x:Static settings:ThemeMode.Default}\"",
            settingsView,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"Auto\"", settingsView, StringComparison.Ordinal);
        Assert.DoesNotContain("GetColor", resourceLookup, StringComparison.Ordinal);
        Assert.DoesNotContain("#00000000", resourceLookup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VectorImageFillBindingsAreLimitedToFixedStatusSemantics()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var fillBindingOwners = Directory
            .EnumerateFiles(Path.Combine(desktopRoot, "Views"), "*.axaml", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                "Fill=\\\"\\{Binding [^\\\"]+\\.Fill\\}\\\""))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "src/DownKyi.Desktop/Views/Dialogs/AlertDialog.axaml",
                "src/DownKyi.Desktop/Views/VideoDetailOperationStatusView.axaml"
            ],
            fillBindingOwners);
    }

    [Fact]
    public void FluentThemeDependencyStaysInsideDesktopProject()
    {
        var consumers = Directory
            .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .Where(path => File.ReadAllText(path).Contains("Avalonia.Themes.Fluent", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(["src/DownKyi.Desktop/DownKyi.Desktop.csproj"], consumers);
    }

    [Theory]
    [InlineData("src", "DownKyi.Desktop", "Views", "DownloadManager", "ViewDownloading.axaml")]
    [InlineData("src", "DownKyi.Desktop", "Views", "DownloadManager", "ViewDownloadFinished.axaml")]
    [InlineData("src", "DownKyi.Desktop", "Views", "ViewMyHistory.axaml")]
    [InlineData("src", "DownKyi.Desktop", "Views", "ViewMyToViewVideo.axaml")]
    [InlineData("src", "DownKyi.Desktop", "Views", "ViewPublicFavorites.axaml")]
    public void LargeListsKeepVirtualizingPanels(params string[] pathParts)
    {
        Assert.Contains("VirtualizingStackPanel", ReadSource(pathParts), StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. pathParts]));
    }

    private static IEnumerable<string> ExtractKeys(string source)
    {
        return Regex.Matches(source, "x:Key=\\\"(?<key>[^\\\"]+)\\\"")
            .Select(static match => match.Groups["key"].Value);
    }

    private static IEnumerable<string> ExtractSemanticResourceReferences(string source)
    {
        return Regex.Matches(
                source,
                "\\{(?:Dynamic|Static)Resource\\s+(?<key>(?:Color|Brush)[A-Za-z0-9]+)\\s*\\}")
            .Select(static match => match.Groups["key"].Value);
    }

    private static bool IsRepositorySourcePath(string path)
    {
        var relativePath = Path.GetRelativePath(RepositoryRoot, path);
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return !segments.Contains(".git", StringComparer.OrdinalIgnoreCase) &&
               !segments.Contains(".tools", StringComparer.OrdinalIgnoreCase) &&
               !segments.Contains("bin", StringComparer.OrdinalIgnoreCase) &&
               !segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }
}
