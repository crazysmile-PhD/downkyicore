using System.Diagnostics;

namespace DownKyi.CentralTestRunner;

internal static class TestInvocationFactory
{
    internal static ProcessStartInfo CreateVstestStartInfo(
        string projectPath,
        CommandOptions options,
        string? resultsDirectory,
        string trxName)
    {
        var startInfo = CreateDotnetStartInfo();
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(options.Configuration);
        if (options.NoRestore)
        {
            startInfo.ArgumentList.Add("--no-restore");
        }
        startInfo.ArgumentList.Add("--no-build");

        var filter = options.Filter;
        if (string.IsNullOrWhiteSpace(filter) && options.Classes.Length > 0)
        {
            filter = string.Join(
                '|',
                options.Classes
                    .Order(StringComparer.Ordinal)
                    .Distinct(StringComparer.Ordinal)
                    .Select(className => $"FullyQualifiedName~{className}"));
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add(filter);
        }
        if (resultsDirectory is not null)
        {
            startInfo.ArgumentList.Add("--logger");
            startInfo.ArgumentList.Add($"trx;LogFileName={trxName}");
            startInfo.ArgumentList.Add("--results-directory");
            startInfo.ArgumentList.Add(resultsDirectory);
        }

        return startInfo;
    }

    internal static ProcessStartInfo CreateInProcessXunitStartInfo(
        string projectPath,
        string targetFramework,
        CommandOptions options,
        string? trxPath)
    {
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            throw new InvalidOperationException(
                "The xUnit in-process runner requires --class locators instead of a VSTest filter.");
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException("The test project directory is unavailable.");
        var assemblyName = Path.GetFileNameWithoutExtension(projectPath);
        var assemblyPath = Path.Combine(
            projectDirectory,
            "bin",
            options.Configuration,
            targetFramework,
            $"{assemblyName}.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("The xUnit in-process test assembly is missing.", assemblyPath);
        }

        var startInfo = CreateDotnetStartInfo();
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("-noLogo");
        startInfo.ArgumentList.Add("-noColor");
        startInfo.ArgumentList.Add("-noAutoReporters");
        startInfo.ArgumentList.Add("-reporter");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("-parallel");
        startInfo.ArgumentList.Add("none");
        foreach (var className in options.Classes.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add("-class");
            startInfo.ArgumentList.Add(className);
        }
        if (trxPath is not null)
        {
            startInfo.ArgumentList.Add("-trx");
            startInfo.ArgumentList.Add(trxPath);
        }

        return startInfo;
    }

    private static ProcessStartInfo CreateDotnetStartInfo()
    {
        return new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }
}
