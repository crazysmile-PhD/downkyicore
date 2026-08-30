using System.Diagnostics;
using System.Text.Json;
using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class BoundedProcessRunnerTests
{
    [Fact]
    public void ParentExitWithInheritedStreamsUsesOwnedTreeCleanup()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var readyPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-bounded-runner-tree-{Guid.NewGuid():N}.json");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("--exit-with-owned-descendant");
        startInfo.ArgumentList.Add(readyPath);

        try
        {
            var failure = Assert.Throws<OwnedProcessExecutionException>(
                () => BoundedProcessRunner.Run(
                    startInfo,
                    TestContext.Current.CancellationToken,
                    TimeSpan.FromSeconds(1)));

            Assert.Equal(OwnedProcessFailureKind.OwnedTreeNotQuiescent, failure.Failure.Kind);
            Assert.False(failure.Failure.TreeQuiescent);
            Assert.Empty(failure.CleanupFailures);
            using var document = JsonDocument.Parse(File.ReadAllText(readyPath));
            var childProcessId = document.RootElement.GetProperty("ChildProcessId").GetInt32();
            AssertProcessExited(childProcessId);
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited, $"Owned descendant {processId} is still running.");
        }
        catch (ArgumentException)
        {
        }
    }
}
