using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;

namespace DownKyi.ProcessSupervision;

internal static class Program
{
    private const string HostMode = "--owned-process-host";

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The executable boundary must emit any host failure as diagnostic evidence and return a nonzero code.")]
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || !string.Equals(args[0], HostMode, StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync(
                    "This executable is an internal owned-process supervisor.")
                .ConfigureAwait(false);
            return 64;
        }

        try
        {
            using var commands = new NamedPipeClientStream(
                ".",
                args[1],
                PipeDirection.In,
                PipeOptions.Asynchronous);
            using var status = new NamedPipeClientStream(
                ".",
                args[2],
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await commands.ConnectAsync().ConfigureAwait(false);
            await status.ConnectAsync().ConfigureAwait(false);

            var result = await SupervisorHost.RunAsync(
                    commands,
                    status,
                    new PlatformSupervisorHostCapability())
                .ConfigureAwait(false);
            if (result.FailSafeFailure != null)
            {
                await Console.Error.WriteLineAsync(result.FailSafeFailure.ToString())
                    .ConfigureAwait(false);
            }
            if (result.ProtocolError != null)
            {
                await Console.Error.WriteLineAsync(result.ProtocolError.ToString())
                    .ConfigureAwait(false);
            }

            return result.Kind == SupervisorHostCompletionKind.Finalized ? 0 : 2;
        }
        catch (Exception failure)
        {
            await Console.Error.WriteLineAsync(failure.ToString()).ConfigureAwait(false);
            return 3;
        }
    }
}
