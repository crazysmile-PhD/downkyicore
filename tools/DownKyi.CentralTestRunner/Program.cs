namespace DownKyi.CentralTestRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "fixture-hold", StringComparison.Ordinal))
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var startTimeUtc = new DateTimeOffset(currentProcess.StartTime.ToUniversalTime());
            Console.WriteLine(
                $"fixture-ready pid={Environment.ProcessId} start={startTimeUtc:O}");
            await Console.Out.FlushAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "fixture-pass", StringComparison.Ordinal))
        {
            Console.WriteLine($"fixture-pass pid={Environment.ProcessId}");
            return 0;
        }

        if (args.Length > 1 && string.Equals(args[0], "fixture-hold-marker", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 4 && string.Equals(args[0], "fixture-sensitive-hold", StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync($"Authorization: Bearer {args[1]}").ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                $"authenticated=https://example.invalid/video?access_token={args[2]}&mid={args[3]}")
                .ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                $"personal-path={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}")
                .ConfigureAwait(false);
            await Console.Error.WriteLineAsync($"Cookie: SESSDATA={args[4]}; bili_jct={args[1]}")
                .ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
            await Console.Error.FlushAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 1 && string.Equals(args[0], "fixture-long-line", StringComparison.Ordinal))
        {
            await Console.Out.WriteAsync($"token={args[1]}{new string('x', 32768)}").ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        return await RunCommandAsync(args, cancellation.Token).ConfigureAwait(false);
    }

    internal static async Task<int> RunCommandAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        return await RunCommandAsync(args, CentralTestCommand.RunAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<int> RunCommandAsync(
        string[] args,
        Func<string[], CancellationToken, Task<int>> runCommandAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runCommandAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
    }
}
