using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class VideoDetailViewArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ViewRoot = Path.Combine(
        RepositoryRoot,
        "src",
        "DownKyi.Desktop",
        "Views");
    private static readonly string[] ViewNames =
    [
        "ViewVideoDetail.axaml",
        "VideoDetailToolbarView.axaml",
        "VideoDetailSummaryView.axaml",
        "VideoDetailSelectionView.axaml",
        "VideoDetailActionsView.axaml"
    ];

    [Fact]
    public void VideoDetailViewRemainsAThinOrderedComposition()
    {
        var source = Read("ViewVideoDetail.axaml");

        Assert.True(File.ReadAllLines(Path.Combine(ViewRoot, "ViewVideoDetail.axaml")).Length <= 60);
        AssertOrdered(
            source,
            "VideoDetailToolbarView",
            "VideoDetailSummaryView",
            "VideoDetailSelectionView",
            "VideoDetailActionsView");
        Assert.Contains("<RowDefinition Height=\"100*\" MaxHeight=\"180\" />", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoDetailOwnersRemainFocusedAndUseOneBindingType()
    {
        foreach (var name in ViewNames)
        {
            var source = Read(name);
            Assert.True(
                File.ReadAllLines(Path.Combine(ViewRoot, name)).Length <= 300,
                $"{name} exceeded the 300-line video-detail view budget.");
            Assert.Contains(
                "x:DataType=\"vm:ViewVideoDetailViewModel\"",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VideoDetailBindingContractRetainsAllMovedTokens()
    {
        var source = string.Join(Environment.NewLine, ViewNames.Select(Read));

        Assert.Equal(56, Count(source, @"\{(?:Reflection)?Binding\s+([^},]+)"));
        Assert.Equal(12, Count(source, @"(?:x:Name|Name)=""([^""]+)"""));
        Assert.Equal(48, Count(source, @"\{DynamicResource\s+([^}]+)\}"));
        Assert.Equal(8, Count(source, @"\{StaticResource\s+([^}]+)\}"));
        Assert.Equal(
            13,
            Count(
                source,
                @"<(?:behavior:[^\s/>]+|(?:Event|Data)TriggerBehavior|InvokeCommandAction|ChangePropertyAction)\b"));
    }

    [Fact]
    public void VideoSectionAndPageGridRemainInsideOneNameScope()
    {
        var source = Read("VideoDetailSelectionView.axaml");

        Assert.Contains("x:Name=\"NameVideoSections\"", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NameVideoPages\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{ReflectionBinding ElementName=NameVideoSections, Path=SelectedItem.VideoPages, Mode=TwoWay}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("VideoPageSelectionBehavior", source, StringComparison.Ordinal);
        Assert.Contains("ResetGridSplitterBehavior", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DataGridRowStylesLayerOnTheApplicationThemeWithoutBaseThemeLookup()
    {
        var selectionSource = Read("VideoDetailSelectionView.axaml");
        var applicationSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Desktop",
            "App.axaml"));

        Assert.Contains("<DataGrid.Styles>", selectionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataGrid.RowTheme>", selectionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BasedOn=", selectionSource, StringComparison.Ordinal);
        Assert.Contains(
            "avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml",
            applicationSource,
            StringComparison.Ordinal);
    }

    private static int Count(string source, string pattern)
    {
        return Regex.Count(
            source,
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromSeconds(1));
    }

    private static void AssertOrdered(string source, params string[] values)
    {
        var index = -1;
        foreach (var value in values)
        {
            var next = source.IndexOf(value, index + 1, StringComparison.Ordinal);
            Assert.True(next > index, $"{value} is missing or out of order.");
            index = next;
        }
    }

    private static string Read(string name)
    {
        return File.ReadAllText(Path.Combine(ViewRoot, name));
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
