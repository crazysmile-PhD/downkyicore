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

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await CentralTestCommand.RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
    }
}
