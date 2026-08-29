using System.Diagnostics;
using System.Text.Json;

namespace DownKyi.Architecture.Tests;

public sealed class CiTestActionBehaviorTests
{
    private const string ExpectedClass =
        "DownKyi.Architecture.Tests.AgentEnvironmentArchitectureTests";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProjectModeExecutesTheCentralRunnerAndValidatesTheSelectedClass()
    {
        var resultsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-ci-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDirectory);
        try
        {
            var startInfo = CreateActionStartInfo(
                Path.Combine(RepositoryRoot, "script", "invoke-ci-test-action.ps1"),
                "Project");
            startInfo.Environment["DOWNKYI_TEST_REPOSITORY_ROOT"] = RepositoryRoot;
            startInfo.Environment["DOWNKYI_TEST_PROJECT_PATH"] = Path.Combine(
                RepositoryRoot,
                "tests",
                "DownKyi.Architecture.Tests",
                "DownKyi.Architecture.Tests.csproj");
            startInfo.Environment["DOWNKYI_TEST_CONFIGURATION"] = "Release";
            startInfo.Environment["DOWNKYI_TEST_NO_RESTORE"] = "true";
            startInfo.Environment["DOWNKYI_TEST_NO_BUILD"] = "true";
            startInfo.Environment["DOWNKYI_TEST_RESULTS_DIRECTORY"] = resultsDirectory;
            startInfo.Environment["DOWNKYI_TEST_TRX_NAME"] = "ci-project.trx";
            startInfo.Environment["DOWNKYI_TEST_EXPECTED_CLASS"] = ExpectedClass;

            var result = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(resultsDirectory, "ci-project.trx")));
            Assert.Contains("ExecutedExpected", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(resultsDirectory, recursive: true);
        }
    }

    [Fact]
    public void SolutionModeBindsNamedParametersAcrossPowerShellPlatforms()
    {
        var capture = ExecuteSolutionMode(mutateDelegation: false);

        Assert.Equal("Release", capture.GetProperty("configuration").GetString());
        Assert.True(capture.GetProperty("noRestore").GetBoolean());
        Assert.True(capture.GetProperty("noBuild").GetBoolean());
        Assert.Equal("action-results", capture.GetProperty("resultsDirectory").GetString());
    }

    [Fact]
    public void DelegationMutationCannotProduceAFalseGreenSolutionGate()
    {
        Assert.Throws<InvalidDataException>(() =>
            ExecuteSolutionMode(mutateDelegation: true));
    }

    [Fact]
    public void DelegatedScopePreservesBoundLifecycleParameters()
    {
        var helperPath = Path.Combine(
            RepositoryRoot,
            "script",
            "delegated-cgroup-scope.ps1");
        var command =
            $". '{helperPath.Replace("'", "''", StringComparison.Ordinal)}'; " +
            "$arguments = ConvertTo-DownKyiPowerShellArgumentList ([ordered]@{ " +
            "Configuration = 'Release'; AssemblyPattern = @('One.Tests', 'Two.Tests'); " +
            "ValidateForensics = [switch]::new($true); NoBuild = [switch]::new($false) }); " +
            "$arguments | ConvertTo-Json -Compress";
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
        startInfo.ArgumentList.Add(command);

        var result = BoundedProcessRunner.Run(
            startInfo,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(
            ["-Configuration", "Release", "-AssemblyPattern", "One.Tests", "Two.Tests", "-ValidateForensics"],
            document.RootElement.EnumerateArray()
                .Select(value => value.GetString()
                    ?? throw new InvalidDataException("A delegated argument was null."))
                .ToArray());
    }

    [Fact]
    public void DirectSolutionEntryAcquiresDelegatedScopeBeforeLoadingTheRunner()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-direct-solution-delegation-{Guid.NewGuid():N}");
        var scriptDirectory = Path.Combine(directory, "script");
        Directory.CreateDirectory(scriptDirectory);
        var capturePath = Path.Combine(directory, "capture.json");
        try
        {
            File.Copy(
                Path.Combine(RepositoryRoot, "script", "test-solution.ps1"),
                Path.Combine(scriptDirectory, "test-solution.ps1"));
            File.WriteAllText(
                Path.Combine(scriptDirectory, "delegated-cgroup-scope.ps1"),
                """
                function ConvertTo-DownKyiPowerShellArgumentList {
                    param([System.Collections.IDictionary]$BoundParameters)
                    $arguments = @()
                    foreach ($entry in $BoundParameters.GetEnumerator()) {
                        $arguments += "-$($entry.Key)"
                        if ($entry.Value -is [System.Management.Automation.SwitchParameter]) { continue }
                        $arguments += [string]$entry.Value
                    }
                    return $arguments
                }
                function Test-DownKyiDelegatedCgroupScopeRequired { return $true }
                function Invoke-DownKyiDelegatedCgroupScope {
                    param([string]$ScriptPath, [string[]]$ArgumentList)
                    @{
                        scriptPath = $ScriptPath
                        arguments = $ArgumentList
                    } | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:DOWNKYI_CAPTURE
                }
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-File",
                         Path.Combine(scriptDirectory, "test-solution.ps1"),
                         "-Configuration", "Debug", "-NoRestore", "-NoBuild",
                         "-ResultsDirectory", "delegated-results"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["DOWNKYI_CAPTURE"] = capturePath;

            var result = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(capturePath));
            using var document = JsonDocument.Parse(File.ReadAllText(capturePath));
            var arguments = document.RootElement.GetProperty("arguments")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Contains("-Configuration", arguments);
            Assert.Contains("Debug", arguments);
            Assert.Contains("-NoRestore", arguments);
            Assert.Contains("-NoBuild", arguments);
            Assert.Contains("-ResultsDirectory", arguments);
            Assert.Contains("delegated-results", arguments);
            Assert.EndsWith(
                Path.Combine("script", "test-solution.ps1"),
                document.RootElement.GetProperty("scriptPath").GetString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MalformedBooleanInputFailsClosedBeforeProjectExecution()
    {
        var startInfo = CreateActionStartInfo(
            Path.Combine(RepositoryRoot, "script", "invoke-ci-test-action.ps1"),
            "Project");
        startInfo.Environment["DOWNKYI_TEST_REPOSITORY_ROOT"] = RepositoryRoot;
        startInfo.Environment["DOWNKYI_TEST_PROJECT_PATH"] = Path.Combine(
            RepositoryRoot,
            "tests",
            "DownKyi.Architecture.Tests",
            "DownKyi.Architecture.Tests.csproj");
        startInfo.Environment["DOWNKYI_TEST_CONFIGURATION"] = "Release";
        startInfo.Environment["DOWNKYI_TEST_NO_RESTORE"] = "TRUE";
        startInfo.Environment["DOWNKYI_TEST_NO_BUILD"] = "true";
        startInfo.Environment["DOWNKYI_TEST_RESULTS_DIRECTORY"] = string.Empty;
        startInfo.Environment["DOWNKYI_TEST_TRX_NAME"] = string.Empty;
        startInfo.Environment["DOWNKYI_TEST_EXPECTED_CLASS"] = string.Empty;

        var result = BoundedProcessRunner.Run(
            startInfo,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be exactly 'true' or 'false'", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedProjectGateDoesNotWriteItsParentsLifecycleMarker()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-ci-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var markerPath = Path.Combine(directory, "parent-marker.jsonl");
        try
        {
            var startInfo = CreateActionStartInfo(
                Path.Combine(RepositoryRoot, "script", "invoke-ci-test-action.ps1"),
                "Project");
            startInfo.Environment["DOWNKYI_TEST_REPOSITORY_ROOT"] = RepositoryRoot;
            startInfo.Environment["DOWNKYI_TEST_PROJECT_PATH"] = Path.Combine(
                RepositoryRoot,
                "tests",
                "DownKyi.Architecture.Tests",
                "DownKyi.Architecture.Tests.csproj");
            startInfo.Environment["DOWNKYI_TEST_CONFIGURATION"] = "Release";
            startInfo.Environment["DOWNKYI_TEST_NO_RESTORE"] = "true";
            startInfo.Environment["DOWNKYI_TEST_NO_BUILD"] = "true";
            startInfo.Environment["DOWNKYI_TEST_RESULTS_DIRECTORY"] = directory;
            startInfo.Environment["DOWNKYI_TEST_TRX_NAME"] = "nested.trx";
            startInfo.Environment["DOWNKYI_TEST_EXPECTED_CLASS"] = ExpectedClass;
            startInfo.Environment["DOWNKYI_LIFECYCLE_MARKER"] = markerPath;

            var result = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonElement ExecuteSolutionMode(bool mutateDelegation)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-ci-solution-{Guid.NewGuid():N}");
        var scriptDirectory = Path.Combine(directory, "script");
        Directory.CreateDirectory(scriptDirectory);
        var capturePath = Path.Combine(directory, "capture.json");
        try
        {
            var actionScript = File.ReadAllText(Path.Combine(
                RepositoryRoot,
                "script",
                "invoke-ci-test-action.ps1"));
            if (mutateDelegation)
            {
                const string invocation =
                    "& (Join-Path $PSScriptRoot \"test-solution.ps1\") @solutionParameters";
                Assert.Contains(invocation, actionScript, StringComparison.Ordinal);
                actionScript = actionScript.Replace(invocation, "return", StringComparison.Ordinal);
            }

            var actionPath = Path.Combine(scriptDirectory, "invoke-ci-test-action.ps1");
            File.WriteAllText(actionPath, actionScript);
            File.Copy(
                Path.Combine(RepositoryRoot, "script", "delegated-cgroup-scope.ps1"),
                Path.Combine(scriptDirectory, "delegated-cgroup-scope.ps1"));
            File.WriteAllText(
                Path.Combine(scriptDirectory, "test-solution.ps1"),
                """
                [CmdletBinding()]
                param(
                    [ValidateSet("Debug", "Release")]
                    [string]$Configuration,
                    [switch]$NoRestore,
                    [switch]$NoBuild,
                    [string]$ResultsDirectory
                )
                @{
                    configuration = $Configuration
                    noRestore = [bool]$NoRestore
                    noBuild = [bool]$NoBuild
                    resultsDirectory = $ResultsDirectory
                } | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:DOWNKYI_CAPTURE
                """);

            var startInfo = CreateActionStartInfo(actionPath, "Solution");
            startInfo.Environment["DOWNKYI_TEST_RESULTS_DIRECTORY"] = "action-results";
            startInfo.Environment["DOWNKYI_CAPTURE"] = capturePath;
            var result = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);
            if (result.ExitCode != 0 || !File.Exists(capturePath))
            {
                throw new InvalidDataException(
                    $"The solution action did not delegate to its central runner. {result.Output}");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(capturePath));
            return document.RootElement.Clone();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessStartInfo CreateActionStartInfo(string scriptPath, string mode)
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
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Mode");
        startInfo.ArgumentList.Add(mode);
        return startInfo;
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
