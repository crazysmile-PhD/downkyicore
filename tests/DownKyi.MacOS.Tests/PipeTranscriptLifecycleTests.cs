using System.Diagnostics;
using System.Globalization;

namespace DownKyi.MacOS.Tests;

public sealed class PipeTranscriptLifecycleTests
{
    [Fact]
    public async Task ReadsCompleteChildTranscriptAfterInheritedPipeHolderExits()
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "/bin/sleep 600 & child=$!; printf 'transcript-ready pid=%s\\n' \"$child\"; exec 1>&-; wait; exit 0");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The transcript fixture did not start.");
        Process? pipeHolder = null;
        try
        {
            var line = await process.StandardOutput
                .ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotNull(line);
            const string prefix = "transcript-ready pid=";
            Assert.StartsWith(prefix, line, StringComparison.Ordinal);
            pipeHolder = Process.GetProcessById(int.Parse(line[prefix.Length..], CultureInfo.InvariantCulture));
            Assert.False(pipeHolder.HasExited);

            var tail = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.False(tail.IsCompleted);
            pipeHolder.Kill();
            await pipeHolder.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await tail.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (pipeHolder is not null)
            {
                if (!pipeHolder.HasExited)
                {
                    pipeHolder.Kill();
                }

                pipeHolder.Dispose();
            }

            if (!process.HasExited)
            {
                process.Kill();
            }

            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }
}
