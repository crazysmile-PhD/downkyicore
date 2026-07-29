using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class NetworkSettingsViewArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ViewRoot = Path.Combine(
        RepositoryRoot,
        "src",
        "DownKyi.Desktop",
        "Views",
        "Settings");
    private static readonly string[] ViewNames =
    [
        "ViewNetwork.axaml",
        "NetworkGeneralSettingsView.axaml",
        "BuiltinDownloaderSettingsView.axaml",
        "AriaDownloaderSettingsView.axaml",
        "CustomAriaSettingsView.axaml"
    ];

    [Fact]
    public void NetworkSettingsViewRemainsAThinOrderedComposition()
    {
        var source = Read("ViewNetwork.axaml");

        Assert.True(File.ReadAllLines(Path.Combine(ViewRoot, "ViewNetwork.axaml")).Length <= 40);
        AssertOrdered(
            source,
            "NetworkGeneralSettingsView",
            "BuiltinDownloaderSettingsView",
            "AriaDownloaderSettingsView",
            "CustomAriaSettingsView");
        Assert.DoesNotContain("{Binding", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkSettingsSectionsRemainFocusedAndUseOneBindingType()
    {
        foreach (var name in ViewNames)
        {
            var source = Read(name);
            Assert.True(
                File.ReadAllLines(Path.Combine(ViewRoot, name)).Length <= 300,
                $"{name} exceeded the 300-line settings-view budget.");
            Assert.Contains(
                "x:DataType=\"vms:ViewNetworkViewModel\"",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NetworkSettingsBindingContractRetainsAllMovedTokens()
    {
        var source = string.Join(Environment.NewLine, ViewNames.Select(Read));

        Assert.Equal(94, Count(source, @"\{Binding\s+([^},]+)"));
        Assert.Equal(40, Count(source, @"(?:x:Name|Name)=""([^""]+)"""));
        Assert.Equal(72, Count(source, @"\{DynamicResource\s+([^}]+)\}"));
        Assert.Equal(4, Count(source, @"\{StaticResource\s+([^}]+)\}"));
        Assert.Equal(26, Count(source, @"CommandParameter=""([^""]+)"""));
    }

    [Fact]
    public void AriaProxyElementReferenceRemainsInsideOneNameScope()
    {
        var source = Read("AriaDownloaderSettingsView.axaml");

        Assert.Contains("Name=\"NameIsAriaHttpProxy\"", source, StringComparison.Ordinal);
        Assert.Contains("Name=\"NameAriaHttpProxyPanel\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "IsVisible=\"{Binding ElementName=NameIsAriaHttpProxy,Path=IsChecked}\"",
            source,
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
