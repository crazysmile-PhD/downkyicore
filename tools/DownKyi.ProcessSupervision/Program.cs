namespace DownKyi.ProcessSupervision;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var result = await SupervisorHost.RunIfRequestedAsync(args).ConfigureAwait(false);
        return result ?? 2;
    }
}
