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
        var mediaStage = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "DownloadMediaStage.cs"));
        var settingsView = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Desktop",
            "Views",
            "Settings",
            "NetworkGeneralSettingsView.axaml"));

        Assert.Contains("RequireSecureTransferSchemes", mediaStage, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "http://\" + url[\"https://",
            mediaStage,
            StringComparison.Ordinal);
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
            Path.Combine("DownKyi.Core", "Settings", "Models", "NetworkSettings.cs"),
            migrationSource.File);
        Assert.Contains("private AllowStatus LegacyUseSsl", migrationSource.Source, StringComparison.Ordinal);
        Assert.Contains("[JsonProperty(\"UseSsl\")]", migrationSource.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void AriaControlPlaneKeepsSecretsOutOfProcessArgumentsAndGlobalHeaders()
    {
        var server = ReadProductionSource(
            "DownKyi.Core",
            "Aria2cNet",
            "Server",
            "AriaServer.cs");
        var config = ReadProductionSource(
            "DownKyi.Core",
            "Aria2cNet",
            "Server",
            "AriaConfig.cs");
        var client = ReadProductionSource(
            "DownKyi.Core",
            "Aria2cNet",
            "Client",
            "AriaClient.cs");
        var backend = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "Aria2TransferBackend.cs");
        var addressResolver = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "AriaDownloadAddressResolver.cs");
        var factory = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "DownloadRuntimeFactory.cs");
        var customSettings = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Views",
            "Settings",
            "CustomAriaSettingsView.axaml");

        Assert.Contains("ArgumentList.Add", server, StringComparison.Ordinal);
        Assert.DoesNotContain("StartInfo.Arguments", server, StringComparison.Ordinal);
        Assert.DoesNotContain("--rpc-secret=", server, StringComparison.Ordinal);
        Assert.DoesNotContain("--header=", server, StringComparison.Ordinal);
        Assert.DoesNotContain("Headers", config, StringComparison.Ordinal);
        Assert.Contains("AriaRpcSecretFile.Create", server, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", client, StringComparison.Ordinal);
        Assert.Contains("!hostUri.IsLoopback", client, StringComparison.Ordinal);
        Assert.Contains("AriaTaskHeaderPolicy.Create", addressResolver, StringComparison.Ordinal);
        Assert.Contains("LocalAriaRpcEndpoint.Create", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("\"downkyi\"", factory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PasswordChar=\"*\"", customSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void AriaHttpsProxyAndPreflightCannotBroadenTheTransferScope()
    {
        var sendOption = ReadProductionSource(
            "DownKyi.Core",
            "Aria2cNet",
            "Client",
            "Entity",
            "AriaSendData.cs");
        var ariaOption = ReadProductionSource(
            "DownKyi.Core",
            "Aria2cNet",
            "Client",
            "Entity",
            "AriaOption.cs");
        var addressResolver = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "AriaDownloadAddressResolver.cs");
        var backend = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "Aria2TransferBackend.cs");
        var runtimeLifecycle = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "Aria2RuntimeLifecycle.cs");
        var proxyPolicy = ReadProductionSource(
            "DownKyi.Core",
            "Settings",
            "AriaHttpsProxyPolicy.cs");
        var redirectRuntimeTests = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tests",
            "DownKyi.Tests",
            "Aria2TlsRedirectIntegrationTests.cs"));
        var tlsRuntime = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tests",
            "DownKyi.Tests",
            "Aria2TlsTestRuntime.cs"));

        Assert.Contains("[JsonProperty(\"https-proxy\")]", sendOption, StringComparison.Ordinal);
        Assert.DoesNotContain("all-proxy", sendOption, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("all-proxy", ariaOption, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-redirect", sendOption, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AllowAutoRedirect = false", addressResolver, StringComparison.Ordinal);
        Assert.Contains("download.transfer.insecure-redirect", addressResolver, StringComparison.Ordinal);
        Assert.Contains("MaximumRedirects", addressResolver, StringComparison.Ordinal);
        Assert.Contains("IsSameOrigin(current, next)", addressResolver, StringComparison.Ordinal);
        Assert.True(
            backend.IndexOf("_addressResolver.ResolveAsync", StringComparison.Ordinal)
            < backend.IndexOf("_ariaClient.AddUriAsync", StringComparison.Ordinal),
            "The defense-in-depth redirect preflight must run before aria2 receives the address.");
        Assert.Contains(
            "downkyi-secure-redirect-v2",
            runtimeLifecycle,
            StringComparison.Ordinal);
        Assert.Contains(
            "downkyi-secure-redirect-v2",
            tlsRuntime,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--ca-certificate=", tlsRuntime, StringComparison.Ordinal);
        Assert.Contains("RunPreflightThenActualDowngradeRejectedAsync", redirectRuntimeTests, StringComparison.Ordinal);
        Assert.Contains("RunHeadSafeGetDowngradeRejectedAsync", redirectRuntimeTests, StringComparison.Ordinal);
        Assert.Contains("RunRangeDowngradeRejectedAsync", redirectRuntimeTests, StringComparison.Ordinal);
        Assert.Contains("RunSecondRoundDowngradeRejectedAsync", redirectRuntimeTests, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(0, target.RequestCount)", redirectRuntimeTests, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(0, target.ConnectionCount)", redirectRuntimeTests, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", proxyPolicy, StringComparison.Ordinal);
        Assert.Contains("localhost", proxyPolicy, StringComparison.Ordinal);
        Assert.Contains("::1", proxyPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkCredential", proxyPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void AriaStartupAndResumeSecurityContractsRemainFailClosed()
    {
        var applicationSettings = ReadProductionSource(
            "DownKyi.Core",
            "Settings",
            "ApplicationSettings.cs");
        var processSupervisor = ReadProductionSource(
            "DownKyi.Core",
            "Aria2cNet",
            "Server",
            "AriaProcessSupervisor.cs");
        var runtimeLifecycle = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "Aria2RuntimeLifecycle.cs");
        var backend = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "Aria2TransferBackend.cs");
        var baseline = ReadProductionSource(
            "docs",
            "operations",
            "aria2-security-baseline.json");
        var tlsRuntime = ReadProductionSource(
            "tests",
            "DownKyi.Tests",
            "Aria2TlsTestRuntime.cs");

        Assert.Contains("uri.UserInfo", applicationSettings, StringComparison.Ordinal);
        Assert.Contains(
            "GetAriaVersionAsync(cancellationToken)",
            runtimeLifecycle,
            StringComparison.Ordinal);
        Assert.Contains("WaitForExit(KillWaitMilliseconds)", processSupervisor, StringComparison.Ordinal);
        Assert.True(
            backend.IndexOf("ChangeOptionAsync(gid, options)", StringComparison.Ordinal)
            < backend.IndexOf("UnpauseAsync(gid)", StringComparison.Ordinal),
            "Existing aria2 task headers must be replaced before the task is resumed.");
        Assert.Contains("windows-system-root-store", baseline, StringComparison.Ordinal);
        Assert.Contains("windows-local-machine-root-store", tlsRuntime, StringComparison.Ordinal);
        Assert.Contains("windows-current-user-root-store", tlsRuntime, StringComparison.Ordinal);
        Assert.Contains("linux-system-ca-store", baseline, StringComparison.Ordinal);
        Assert.Contains("macos-system-keychain", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedAria2IsVerifiedBeforeProcessStartAndCustomAriaRequiresTheFeature()
    {
        var server = ReadProductionSource(
            "DownKyi.Core",
            "Aria2cNet",
            "Server",
            "AriaServer.cs");
        var verifier = ReadProductionSource(
            "DownKyi.Core",
            "Aria2cNet",
            "Server",
            "AriaBinaryIntegrityVerifier.cs");
        var backend = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "Aria2TransferBackend.cs");
        var runtimeLifecycle = ReadProductionSource(
            "src",
            "DownKyi.Desktop",
            "Services",
            "Download",
            "Aria2RuntimeLifecycle.cs");
        var powerShellInstaller = ReadProductionSource("script", "aria2.ps1");
        var shellInstaller = ReadProductionSource("script", "aria2.sh");
        var qualityWorkflow = ReadProductionSource(".github", "workflows", "quality.yml");

        Assert.Contains("AppContext.BaseDirectory", server, StringComparison.Ordinal);
        Assert.True(
            server.IndexOf("AriaBinaryIntegrityVerifier.Verify", StringComparison.Ordinal)
            < server.IndexOf("ExecuteProcess(executablePath", StringComparison.Ordinal),
            "The packaged binary digest must be verified before Process.Start can be reached.");
        Assert.Contains("CryptographicOperations.FixedTimeEquals", verifier, StringComparison.Ordinal);
        Assert.Contains("ChecksumSidecarSuffix", verifier, StringComparison.Ordinal);
        Assert.Contains("_runtimeLifecycle.StartAsync", backend, StringComparison.Ordinal);
        Assert.Contains("EnsureSecureRedirectFeatureAsync", runtimeLifecycle, StringComparison.Ordinal);
        Assert.Contains("EnsureSecureRedirectFeature(versionResult.EnabledFeatures)", runtimeLifecycle, StringComparison.Ordinal);
        Assert.Contains("binarySha256", powerShellInstaller, StringComparison.Ordinal);
        Assert.Contains("aria2c.exe.sha256", powerShellInstaller, StringComparison.Ordinal);
        Assert.Contains("binarySha256", shellInstaller, StringComparison.Ordinal);
        Assert.Contains("aria2c.sha256", shellInstaller, StringComparison.Ordinal);
        Assert.Contains(
            "run: bash ./script/aria2.sh '${{ matrix.asset-argument }}'",
            qualityWorkflow,
            StringComparison.Ordinal);
    }

    private static string ReadProductionSource(params string[] segments)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));
    }

    private static IEnumerable<string> EnumerateProductionTextFiles()
    {
        var roots = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi"),
            Path.Combine(RepositoryRoot, "DownKyi.Core"),
            Path.Combine(RepositoryRoot, "src"),
            Path.Combine(RepositoryRoot, "script"),
            Path.Combine(RepositoryRoot, ".github", "workflows")
        };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".axaml",
            ".json",
            ".ps1",
            ".sh",
            ".yml",
            ".yaml"
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
