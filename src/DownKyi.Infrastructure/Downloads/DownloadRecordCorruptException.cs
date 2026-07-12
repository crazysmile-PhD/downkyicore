namespace DownKyi.Infrastructure.Downloads;

public sealed class DownloadRecordCorruptException : IOException
{
    public DownloadRecordCorruptException()
    {
        FieldName = string.Empty;
    }

    public DownloadRecordCorruptException(string message)
        : base(message)
    {
        FieldName = string.Empty;
    }

    public DownloadRecordCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
        FieldName = string.Empty;
    }

    public DownloadRecordCorruptException(string fieldName, string reason, Exception? innerException = null)
        : base(reason, innerException)
    {
        FieldName = fieldName;
    }

    public string FieldName { get; }
}
