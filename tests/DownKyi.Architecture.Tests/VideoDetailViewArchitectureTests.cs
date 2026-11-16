using System.Xml.Linq;

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
        "VideoDetailActionsView.axaml",
        "VideoDetailOperationStatusView.axaml"
    ];

    [Fact]
    public void VideoDetailSectionsUseOneBindingType()
    {
        foreach (var name in ViewNames)
        {
            var source = Read(name);
            Assert.Contains(
                "x:DataType=\"vm:VideoDetailViewModel\"",
                source,
                StringComparison.Ordinal);
        }
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
        var grid = XDocument.Parse(selectionSource).Descendants()
            .Single(element => element.Name.LocalName == "DataGrid");
        Assert.DoesNotContain("BasedOn=", grid.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml",
            applicationSource,
            StringComparison.Ordinal);
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
