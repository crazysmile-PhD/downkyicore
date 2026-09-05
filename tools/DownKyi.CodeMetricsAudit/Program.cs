using System.Text.Json;

namespace DownKyi.CodeMetricsAudit;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var options = AuditOptions.Parse(args);
            var gitState = await GitStateReader.ReadAsync(options.RepositoryRoot).ConfigureAwait(false);
            var report = Ca1506ReportGenerator.Generate(
                options.RepositoryRoot,
                options.SarifDirectory,
                options.ClassificationFile,
                gitState);
            Ca1506ReportWriter.Write(options.OutputDirectory, report);

            await output.WriteLineAsync(
                $"CA1506 audit completed with {report.Findings.Count} advisory finding(s).").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Production: {report.Summary.Production}; test: {report.Summary.Test}").ConfigureAwait(false);
            await output.WriteLineAsync(
                "CA1506 reports: ca1506-report.md; ca1506-report.json").ConfigureAwait(false);
            return 0;
        }
        catch (JsonException)
        {
            await error.WriteLineAsync("CA1506 audit failed: malformed JSON input.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"CA1506 audit failed: {GetFailureCategory(exception)}.").ConfigureAwait(false);
            return 1;
        }
    }

    private static string GetFailureCategory(Exception exception)
    {
        return exception switch
        {
            ArgumentException => "invalid arguments or missing input",
            InvalidDataException => "invalid audit input",
            UnauthorizedAccessException => "access denied",
            IOException => "audit I/O failure",
            InvalidOperationException => "audit infrastructure failure",
            _ => "unexpected controlled failure"
        };
    }
}
