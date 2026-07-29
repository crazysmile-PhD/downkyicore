namespace DownKyi.Application.Diagnostics;

public interface IApplicationLogService
{
    string LogDirectory { get; }

    IReadOnlyList<ApplicationLogRecord> GetRecentEvents();

    ApplicationLogMetrics GetMetrics();

    Task FlushAsync(CancellationToken cancellationToken = default);

    Task<string> ExportDiagnosticLogAsync(CancellationToken cancellationToken = default);
}
