using BenchmarkDotNet.Running;

namespace DownKyi.Benchmarks;

internal static class Program
{
    public static void Main(string[] args)
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "downkyi-benchmarks",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("DOWNKYI_DATA_DIR", dataRoot);

        BenchmarkSwitcher.FromAssembly(typeof(RequestPreparationBenchmarks).Assembly).Run(args);
    }
}
