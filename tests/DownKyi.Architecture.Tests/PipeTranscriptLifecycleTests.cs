using System.Diagnostics;

namespace DownKyi.Architecture.Tests;

public sealed class PipeTranscriptLifecycleTests
{
    [Fact]
    public async Task ReadsCompleteChildTranscript()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("/bin/sleep 600 & printf 'transcript-ready\n'; exec 1>&-; wait");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The transcript fixture did not start.");

        Assert.Equal("transcript-ready", await process.StandardOutput
            .ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await process.StandardOutput
            .ReadToEndAsync(TestContext.Current.CancellationToken));
    }
}
