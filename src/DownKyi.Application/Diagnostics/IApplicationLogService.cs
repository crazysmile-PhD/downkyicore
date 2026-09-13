namespace DownKyi.Application.Diagnostics;

public interface IApplicationLogService
{
    string LogDirectory { get; }

    IReadOnlyList<ApplicationLogRecord> GetRecentEvents();

    ApplicationLogMetrics GetMetrics();

    string RedactDiagnosticText(string? text);

    Task FlushAsync(CancellationToken cancellationToken = default);

    Task<string> ExportDiagnosticLogAsync(CancellationToken cancellationToken = default);
}
