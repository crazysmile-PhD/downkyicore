namespace DownKyi.Domain.Downloads;

public sealed record DownloadTaskId
{
    public DownloadTaskId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
