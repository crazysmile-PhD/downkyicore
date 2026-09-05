namespace DownKyi.CodeMetricsAudit;

internal static class Program
{
    public static async Task<int> Main(string[] args)
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

            await Console.Out.WriteLineAsync(
                $"CA1506 audit completed with {report.Findings.Count} advisory finding(s).").ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                $"Production: {report.Summary.Production}; test: {report.Summary.Test}").ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                $"Markdown: {Path.Combine(options.OutputDirectory, "ca1506-report.md")}").ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                $"JSON: {Path.Combine(options.OutputDirectory, "ca1506-report.json")}").ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"CA1506 audit failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
