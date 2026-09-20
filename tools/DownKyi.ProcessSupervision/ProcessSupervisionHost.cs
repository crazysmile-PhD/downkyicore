namespace DownKyi.ProcessSupervision;

internal static class ProcessSupervisionHost
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 ||
            !string.Equals(args[0], "owned-scope-host", StringComparison.Ordinal))
        {
            return 2;
        }

        return await OwnedProcessScope.RunHostAsync(args[1], args[2]).ConfigureAwait(false);
    }
}
