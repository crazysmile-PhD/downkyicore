namespace DownKyi.Core.Logging;

public interface IApplicationLogService
{
    string LogDirectory { get; }

    IReadOnlyList<ApplicationLogRecord> GetRecentEvents();

    ApplicationLogMetrics GetMetrics();

    Task FlushAsync(CancellationToken cancellationToken = default);

    Task<string> ExportDiagnosticLogAsync(CancellationToken cancellationToken = default);
}
