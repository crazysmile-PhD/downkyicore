using System.Diagnostics;
using DownKyi.CentralTestRunner;
using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerProviderFreshnessTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public void ExistingProviderIsLoadedOnlyAfterTheBuildBoundarySucceeds(int buildExitCode)
    {
        var fixtureRoot = CreateProviderFixture();
        var markerPath = Path.Combine(fixtureRoot, "build-arguments.txt");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                """
                $ErrorActionPreference = 'Stop'
                . $env:DOWNKYI_PROVIDER_SCRIPT
                $buildExitCode = [int]$env:DOWNKYI_FAKE_BUILD_EXIT_CODE
                function global:dotnet {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromRemainingArguments = $true)]
                        [object[]]$Arguments
                    )

                    [IO.File]::WriteAllLines(
                        $env:DOWNKYI_BUILD_MARKER,
                        [string[]]$Arguments)
                    $global:LASTEXITCODE = $buildExitCode
                }

                $failure = $null
                try {
                    Import-DownKyiCentralTestRunner `
                        -RepositoryRoot $env:DOWNKYI_PROVIDER_FIXTURE `
                        -Configuration Release `
                        -BuildIfMissing `
                        -NoRestore
                }
                catch {
                    $failure = $_.Exception
                }

                $providerType =
                    'DownKyi.CentralTestRunner.CentralTestOrchestrator' -as [type]
                if ($buildExitCode -eq 0) {
                    if ($null -ne $failure) {
                        throw $failure
                    }
                    if ($null -eq $providerType) {
                        throw 'The provider was not loaded after a successful build.'
                    }
                }
                else {
                    if ($null -eq $failure) {
                        throw 'The existing provider bypassed a failed build.'
                    }
                    if ($failure.Message -ne 'The compiled central test runner build failed.') {
                        throw $failure
                    }
                    if ($null -ne $providerType) {
                        throw 'The stale provider was loaded after a failed build.'
                    }
                }

                if (-not (Test-Path -LiteralPath $env:DOWNKYI_BUILD_MARKER -PathType Leaf)) {
                    throw 'The provider build boundary was not invoked.'
                }
                """);
            startInfo.Environment["DOWNKYI_PROVIDER_SCRIPT"] = Path.Combine(
                RepositoryRoot,
                "script",
                "test-project-runner.ps1");
            startInfo.Environment["DOWNKYI_PROVIDER_FIXTURE"] = fixtureRoot;
            startInfo.Environment["DOWNKYI_BUILD_MARKER"] = markerPath;
            startInfo.Environment["DOWNKYI_FAKE_BUILD_EXIT_CODE"] =
                buildExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var result = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var arguments = File.ReadAllLines(markerPath);
            Assert.Equal("build", arguments[0]);
            Assert.Contains(
                Path.Combine(
                    fixtureRoot,
                    "tools",
                    "DownKyi.CentralTestRunner",
                    "DownKyi.CentralTestRunner.csproj"),
                arguments);
            Assert.Contains("-nodeReuse:false", arguments);
            Assert.Contains("-p:UseSharedCompilation=false", arguments);
            Assert.Contains("--no-restore", arguments);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static string CreateProviderFixture()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-provider-freshness-{Guid.NewGuid():N}");
        var output = Path.Combine(
            root,
            "tools",
            "DownKyi.CentralTestRunner",
            "bin",
            "Release",
            "net10.0");
        Directory.CreateDirectory(output);
        File.Copy(
            typeof(CentralTestOrchestrator).Assembly.Location,
            Path.Combine(output, "DownKyi.CentralTestRunner.dll"));
        File.Copy(
            typeof(OwnedProcessLease).Assembly.Location,
            Path.Combine(output, "DownKyi.ProcessSupervision.dll"));
        return root;
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
