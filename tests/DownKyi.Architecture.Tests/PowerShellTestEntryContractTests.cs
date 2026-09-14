using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DownKyi.Architecture.Tests;

public sealed class PowerShellTestEntryContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("test-project.ps1", 0)]
    [InlineData("test-project.ps1", 2)]
    [InlineData("test-project.ps1", 37)]
    [InlineData("test-project.ps1", 124)]
    [InlineData("test-project.ps1", 130)]
    [InlineData("test-solution.ps1", 0)]
    [InlineData("test-solution.ps1", 2)]
    [InlineData("test-solution.ps1", 37)]
    [InlineData("test-solution.ps1", 124)]
    [InlineData("test-solution.ps1", 130)]
    public async Task PublicFileEntryPreservesRunnerExitCode(string scriptName, int expectedExitCode)
    {
        var fixtureRoot = CreateTemporaryDirectory();
        try
        {
            var scriptDirectory = Path.Combine(fixtureRoot, "script");
            Directory.CreateDirectory(scriptDirectory);
            File.Copy(
                Path.Combine(RepositoryRoot, "script", scriptName),
                Path.Combine(scriptDirectory, scriptName));
            await File.WriteAllTextAsync(
                Path.Combine(scriptDirectory, "test-project-runner.ps1"),
                FakeInnerRunnerScript,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            var arguments = new List<string>
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-File",
                Path.Combine(scriptDirectory, scriptName)
            };
            if (string.Equals(scriptName, "test-project.ps1", StringComparison.Ordinal))
            {
                arguments.Add("-ProjectPath");
                arguments.Add("fixture.csproj");
            }

            var result = await RunPowerShellAsync(
                arguments,
                fixtureRoot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["DOWNKYI_FAKE_EXIT_CODE"] = expectedExitCode.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                }).ConfigureAwait(true);

            Assert.Equal(expectedExitCode, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            if (expectedExitCode == 0)
            {
                Assert.Equal(string.Empty, result.StandardError);
            }
            else
            {
                Assert.Contains(
                    $"CentralTestRunner exited {expectedExitCode}; evidence:",
                    result.StandardError,
                    StringComparison.Ordinal);
                Assert.Contains("fake-failure.json", result.StandardError, StringComparison.Ordinal);
                Assert.Single(result.StandardError.Split(
                    ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            }
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("test-project.ps1")]
    [InlineData("test-solution.ps1")]
    public async Task FailedDiagnosticWriterCannotChangeCompletedRunnerExit(string scriptName)
    {
        var fixtureRoot = CreateTemporaryDirectory();
        try
        {
            var scriptDirectory = Path.Combine(fixtureRoot, "script");
            Directory.CreateDirectory(scriptDirectory);
            File.Copy(
                Path.Combine(RepositoryRoot, "script", scriptName),
                Path.Combine(scriptDirectory, scriptName));
            await File.WriteAllTextAsync(
                Path.Combine(scriptDirectory, "test-project-runner.ps1"),
                FakeInnerRunnerScript,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            var arguments = new List<string>
            {
                "-NoLogo", "-NoProfile", "-NonInteractive",
                "-File", Path.Combine(scriptDirectory, scriptName)
            };
            if (string.Equals(scriptName, "test-project.ps1", StringComparison.Ordinal))
            {
                arguments.Add("-ProjectPath");
                arguments.Add("fixture.csproj");
            }

            var result = await RunPowerShellAsync(
                arguments,
                fixtureRoot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["DOWNKYI_FAKE_EXIT_CODE"] = "37",
                    ["DOWNKYI_THROW_DIAGNOSTIC"] = "1"
                }).ConfigureAwait(true);

            Assert.Equal(37, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(fixtureRoot, "fake-evidence", "fake-failure.json")));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DotSourcedInnerFunctionsReturnResultsWithoutExitingHost()
    {
        var fixtureRoot = CreateTemporaryDirectory();
        try
        {
            var runnerAssembly = Path.Combine(
                fixtureRoot,
                "tools",
                "DownKyi.CentralTestRunner",
                "bin",
                "Release",
                "net10.0",
                "DownKyi.CentralTestRunner.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(runnerAssembly)!);
            await File.WriteAllTextAsync(
                runnerAssembly,
                "deterministic fake runner assembly",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var projectPath = Path.Combine(fixtureRoot, "fixture.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project />",
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            var innerRunner = Path.Combine(RepositoryRoot, "script", "test-project-runner.ps1");
            var command = $$"""
                $ErrorActionPreference = 'Stop'
                function global:dotnet {
                    if ($args.Count -gt 0 -and [string]$args[0] -eq 'build') {
                        $global:LASTEXITCODE = 0
                        return
                    }

                    Write-Output "fake-dotnet-stdout:$env:DOWNKYI_FAKE_EXIT_CODE"
                    $global:LASTEXITCODE = [int]$env:DOWNKYI_FAKE_EXIT_CODE
                }

                . '{{EscapePowerShellLiteral(innerRunner)}}'
                $projectResult = Invoke-DownKyiTestProject `
                    -RepositoryRoot '{{EscapePowerShellLiteral(fixtureRoot)}}' `
                    -ProjectPath '{{EscapePowerShellLiteral(projectPath)}}' `
                    -Configuration Release
                $solutionResult = Invoke-DownKyiTestSolution `
                    -RepositoryRoot '{{EscapePowerShellLiteral(fixtureRoot)}}' `
                    -Configuration Release
                $payload = [ordered]@{
                    ProjectExitCode = $projectResult.ExitCode
                    ProjectRunner = $projectResult.Runner
                    SolutionExitCode = $solutionResult.ExitCode
                    SolutionRunner = $solutionResult.Runner
                } | ConvertTo-Json -Compress
                [Console]::Out.WriteLine("INNER-RESULT:$payload")
                [Console]::Out.WriteLine('HOST-CONTINUED')
                exit 0
                """;
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

            var result = await RunPowerShellAsync(
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCommand],
                fixtureRoot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["DOWNKYI_FAKE_EXIT_CODE"] = "37"
                }).ConfigureAwait(true);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("HOST-CONTINUED", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-dotnet-stdout:", result.StandardOutput, StringComparison.Ordinal);
            var payloadLine = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("INNER-RESULT:", StringComparison.Ordinal));
            using var payload = JsonDocument.Parse(payloadLine["INNER-RESULT:".Length..]);
            Assert.Equal(37, payload.RootElement.GetProperty("ProjectExitCode").GetInt32());
            Assert.Equal(
                "central-test-runner",
                payload.RootElement.GetProperty("ProjectRunner").GetString());
            Assert.Equal(37, payload.RootElement.GetProperty("SolutionExitCode").GetInt32());
            Assert.Equal(
                "central-test-runner",
                payload.RootElement.GetProperty("SolutionRunner").GetString());
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private const string FakeInnerRunnerScript = """
        function Invoke-DownKyiTestProject {
            $exitCode = [int]$env:DOWNKYI_FAKE_EXIT_CODE
            $evidenceDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'fake-evidence'
            if ($exitCode -ne 0) {
                New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $evidenceDirectory 'fake-failure.json') -Value '{}'
            }
            if ($env:DOWNKYI_THROW_DIAGNOSTIC -eq '1') {
                Add-Type -TypeDefinition @'
                public sealed class ThrowingDiagnosticWriter : System.IO.TextWriter {
                    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
                    public override void WriteLine(string value) {
                        throw new System.IO.IOException("diagnostic writer failed");
                    }
                }
        '@
                [Console]::SetError([ThrowingDiagnosticWriter]::new())
            }
            return [pscustomobject]@{
                ExitCode = $exitCode
                Runner = 'fake-runner'
                TrxPath = $null
                EvidenceDirectory = $evidenceDirectory
            }
        }

        function Invoke-DownKyiTestSolution {
            $exitCode = [int]$env:DOWNKYI_FAKE_EXIT_CODE
            $evidenceDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'fake-evidence'
            if ($exitCode -ne 0) {
                New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $evidenceDirectory 'fake-failure.json') -Value '{}'
            }
            if ($env:DOWNKYI_THROW_DIAGNOSTIC -eq '1') {
                Add-Type -TypeDefinition @'
                public sealed class ThrowingDiagnosticWriter : System.IO.TextWriter {
                    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
                    public override void WriteLine(string value) {
                        throw new System.IO.IOException("diagnostic writer failed");
                    }
                }
        '@
                [Console]::SetError([ThrowingDiagnosticWriter]::new())
            }
            return [pscustomobject]@{
                ExitCode = $exitCode
                Runner = 'fake-runner'
                EvidenceDirectory = $evidenceDirectory
            }
        }
        """;

    private static async Task<ProcessResult> RunPowerShellAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "PowerShell subprocess did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException exception)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(ProcessTimeout).ConfigureAwait(true);
            throw new TimeoutException("PowerShell contract fixture exceeded its deadline.", exception);
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(true),
            await standardError.ConfigureAwait(true));
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"downkyi-powershell-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
