using System.Diagnostics;

namespace DownKyi.Architecture.Tests;

public sealed class FocusedTestCliArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void FocusedCommandOnlyDelegatesTestExecutionToTheSharedRunner()
    {
        var command = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "test-project.ps1"));

        Assert.Contains(
            ". (Join-Path $PSScriptRoot \"test-project-runner.ps1\")",
            command,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(command, "Invoke-DownKyiTestProject"));
        Assert.Contains("-ClassNames @($ClassName)", command, StringComparison.Ordinal);
        Assert.Contains("if ($result.ExitCode -ne 0)", command, StringComparison.Ordinal);

        string[] duplicateExecutionOrPolicyOwners =
        [
            "& dotnet",
            "dotnet test",
            "dotnet vstest",
            "Start-Process",
            "System.Diagnostics.Process",
            "vstest.console",
            "xunit.console",
            "xunit.runner",
            "Get-DownKyiCurrentTestPlatform",
            "Get-DownKyiTestRunnerPolicy",
            "Test-DownKyiTestProjectSupportsPlatform"
        ];
        Assert.All(
            duplicateExecutionOrPolicyOwners,
            owner => Assert.DoesNotContain(owner, command, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidProjectFailsClosedBeforeTestExecution()
    {
        var result = RunFocusedCommand(
            "-ProjectPath",
            "tests/Does.Not.Exist/Does.Not.Exist.csproj",
            "-ClassName",
            GetType().FullName!);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Does.Not.Exist.csproj",
            result.StandardOutput + result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformUnauthorizedProjectFailsClosedBeforeTestExecution()
    {
        var project = OperatingSystem.IsWindows()
            ? "tests/DownKyi.MacOS.Tests/DownKyi.MacOS.Tests.csproj"
            : "tests/DownKyi.Windows.Tests/DownKyi.Windows.Tests.csproj";
        var result = RunFocusedCommand(
            "-ProjectPath",
            project,
            "-ClassName",
            GetType().FullName!,
            "-NoRestore",
            "-NoBuild");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "cannot run on",
            result.StandardOutput + result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string source, string value)
    {
        return source.Split(value, StringSplitOptions.None).Length - 1;
    }

    private static ProcessResult RunFocusedCommand(params string[] arguments)
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
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot, "script", "test-project.ps1"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Focused test CLI fail-closed proof timed out.");
        }

        return new ProcessResult(process.ExitCode, standardOutput, standardError);
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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
