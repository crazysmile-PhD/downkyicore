namespace DownKyi.CentralTestRunner;

internal static class Program
{
    private const int MaximumDiagnosticLength = 16 * 1024;

    public static async Task<int> Main(string[] args)
    {
        var fixtureExitCode = await FixtureHost.TryRunAsync(args).ConfigureAwait(false);
        if (fixtureExitCode.HasValue)
        {
            return fixtureExitCode.Value;
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
            await WriteDiagnosticBestEffortAsync(FormatExceptionDiagnostic(exception)).ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
    }

    internal static string FormatExceptionDiagnostic(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var cleanupContext = string.Join(
            Environment.NewLine,
            BuildProcessCleanupDiagnostics.Describe(exception));
        var exceptionDetail = exception.ToString();
        var diagnostic = string.IsNullOrEmpty(cleanupContext)
            ? exceptionDetail
            : $"{cleanupContext}{Environment.NewLine}{exceptionDetail}";
        var redacted = new SensitiveEvidenceRedactor(Environment.CurrentDirectory).Redact(diagnostic);
        if (redacted.Length <= MaximumDiagnosticLength)
        {
            return redacted;
        }

        return
            $"{redacted[..MaximumDiagnosticLength]}{Environment.NewLine}" +
            $"[diagnostic truncated after {MaximumDiagnosticLength} characters]";
    }

    internal static async Task WriteDiagnosticBestEffortAsync(string message)
    {
        // Diagnostics have no authority over process cleanup or runner exit.
        var write = Task.Run(() =>
        {
            try
            {
                Console.Error.WriteLine(message);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The recorder artifact remains the durable diagnostic source.
            }
        }, CancellationToken.None);
        try
        {
            await write.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // An unavailable stderr sink cannot extend the bounded cleanup path.
        }
    }
}
