namespace DownKyi.Application.Downloads;

public sealed record QuarantinedDownloadRecord(
    long Id,
    string SourceTable,
    string RecordId,
    string FieldName,
    string Reason,
    DateTimeOffset QuarantinedAtUtc);
