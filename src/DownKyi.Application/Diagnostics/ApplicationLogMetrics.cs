namespace DownKyi.Application.Diagnostics;

public sealed record ApplicationLogMetrics(
    long BytesWritten,
    long EventsWritten,
    long DroppedEntries,
    long AgeDeletionCount,
    long CapacityDeletionCount,
    long MaintenanceFailureCount,
    long RetainedBytes,
    double CapacityRatio,
    DateTimeOffset? LastMaintenanceUtc,
    long MalformedExportRecords = 0);
