namespace DownKyi.Architecture.Tests;

public sealed class TlsSecurityArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProductionCodeDoesNotDisableCertificateValidation()
    {
        var forbidden = new[]
        {
            "--check-certificate=" + "false",
            "check-certificate=" + "false",
            "--no-check-" + "certificate",
            "DangerousAccept" + "AnyServerCertificateValidator",
            "ServerCertificate" + "CustomValidationCallback",
            "RemoteCertificate" + "ValidationCallback",
            "NODE_TLS_REJECT_UNAUTHORIZED" + "=0",
            "CURLOPT_SSL_VERIFYPEER" + ", 0",
            "CURLOPT_SSL_VERIFYHOST" + ", 0"
        };

        foreach (var file in EnumerateProductionTextFiles())
        {
            var source = File.ReadAllText(file);
            foreach (var token in forbidden)
            {
                Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void DownloadRuntimeCannotDowngradeHttpsToHttp()
    {
        var mediaStage = ReadProductionSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Runtime", "DownloadMediaStage.cs");
        var settingsView = ReadProductionSource(
            "src", "DownKyi.Desktop", "Views", "Settings", "NetworkGeneralSettingsView.axaml");

        Assert.Contains("RequireSecureTransferSchemes", mediaStage, StringComparison.Ordinal);
        Assert.DoesNotContain("http://\" + url[\"https://", mediaStage, StringComparison.Ordinal);
        Assert.DoesNotContain("UseSslCommand", settingsView, StringComparison.Ordinal);
        Assert.DoesNotContain("NameUseSsl", settingsView, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyUseSslExistsOnlyAtTheOneWaySettingsMigrationBoundary()
    {
        var productionSources = EnumerateProductionTextFiles()
            .Select(file => new
            {
                File = Path.GetRelativePath(RepositoryRoot, file),
                Source = File.ReadAllText(file)
            })
            .Where(item => item.Source.Contains("UseSsl", StringComparison.Ordinal))
            .ToArray();

        var migrationSource = Assert.Single(productionSources);
        Assert.Equal(
            Path.Combine("src", "DownKyi.Infrastructure", "Settings", "Models", "NetworkSettings.cs"),
            migrationSource.File);
        Assert.Contains("private AllowStatus LegacyUseSsl", migrationSource.Source, StringComparison.Ordinal);
        Assert.Contains("[JsonProperty(\"UseSsl\")]", migrationSource.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltinDownloaderOwnsSecureAddressAndRangeValidation()
    {
        var resolver = ReadProductionSource(
            "src", "DownKyi.Infrastructure", "Downloads", "DownloadAddressResolver.cs");
        var downloader = ReadProductionSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Transfer", "BuiltinRangeDownloader.cs");

        Assert.Contains("CreateHeaders", resolver, StringComparison.Ordinal);
        Assert.Contains("Uri.UriSchemeHttps", resolver, StringComparison.Ordinal);
        Assert.Contains("CarriesCredentials", resolver, StringComparison.Ordinal);
        Assert.Contains("ContentRange", downloader, StringComparison.Ordinal);
        Assert.Contains("PartialContent", downloader, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerCertificateCustomValidationCallback", downloader, StringComparison.Ordinal);
    }

    private static string ReadProductionSource(params string[] segments)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));
    }

    private static IEnumerable<string> EnumerateProductionTextFiles()
    {
        var roots = new[]
        {
            Path.Combine(RepositoryRoot, "src"),
            Path.Combine(RepositoryRoot, "script"),
            Path.Combine(RepositoryRoot, ".github", "workflows")
        };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".axaml", ".json", ".ps1", ".sh", ".yml", ".yaml"
        };

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(file => extensions.Contains(Path.GetExtension(file)))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));
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
