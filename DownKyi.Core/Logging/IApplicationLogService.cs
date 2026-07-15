namespace DownKyi.Core.Logging;

public interface IApplicationLogService
{
    string LogDirectory { get; }

    IReadOnlyList<ApplicationLogRecord> GetRecentEvents();

    Task FlushAsync(CancellationToken cancellationToken = default);

    Task<string> ExportDiagnosticLogAsync(CancellationToken cancellationToken = default);
}
