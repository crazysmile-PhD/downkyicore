using System.ComponentModel;
using System.Diagnostics;

namespace DownKyi.Core.FFMpeg;

internal sealed class FfmpegProcessRunner : IFfmpegProcessRunner
{
    public async Task<FfmpegProcessResult> RunAsync(
        FfmpegCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var process = CreateProcess(command);
        try
        {
            if (!process.Start())
            {
                return new FfmpegProcessResult(false, -1, string.Empty, "Process did not start.", false);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
                                                      !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                var timedOutOutput = await CompleteReadAsync(outputTask).ConfigureAwait(false);
                var timedOutError = await CompleteReadAsync(errorTask).ConfigureAwait(false);
                return FfmpegProcessResult.Timeout(timedOutOutput, timedOutError);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new FfmpegProcessResult(process.ExitCode == 0, process.ExitCode, output, error, false);
        }
        catch (Win32Exception e)
        {
            return new FfmpegProcessResult(false, -1, string.Empty, e.Message, false);
        }
        catch (InvalidOperationException e)
        {
            return new FfmpegProcessResult(false, -1, string.Empty, e.Message, false);
        }
    }

    private static Process CreateProcess(FfmpegCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private static async Task<string> CompleteReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (Win32Exception)
        {
            // Process termination is best effort during timeout or cancellation.
        }
    }
}
