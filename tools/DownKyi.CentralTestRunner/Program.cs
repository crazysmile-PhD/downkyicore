namespace DownKyi.CentralTestRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "platform", StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync(CentralTestPolicy.GetCurrentPlatform())
                .ConfigureAwait(false);
            return 0;
        }

        await Console.Error.WriteLineAsync(
                "The compiled central test runner is invoked through script/test-project-runner.ps1.")
            .ConfigureAwait(false);
        return 2;
    }
}
