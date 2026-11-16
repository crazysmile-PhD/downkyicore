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
        "BuiltinDownloaderSettingsView.axaml"
    ];

    [Fact]
    public void NetworkSettingsSectionsUseOneBindingType()
    {
        foreach (var name in ViewNames)
        {
            var source = Read(name);
            Assert.Contains(
                "x:DataType=\"vms:NetworkViewModel\"",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuiltinProxyPanelUsesTheViewModelAsItsSingleVisibilityOwner()
    {
        var source = Read("BuiltinDownloaderSettingsView.axaml");

        Assert.Contains("Name=\"NameIsHttpProxy\"", source, StringComparison.Ordinal);
        Assert.Contains("Name=\"NameHttpProxyPanel\"", source, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsHttpProxy}\"", source, StringComparison.Ordinal);
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
