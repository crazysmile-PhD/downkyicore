namespace DownKyi.Core.BiliApi.VideoStream;

/// <summary>
/// The playback response was accepted, but it contains no usable full media.
/// </summary>
public sealed class PlaybackResourceUnavailableException : PlaybackUnavailableException
{
    public PlaybackResourceUnavailableException()
        : this("unknown")
    {
    }

    public PlaybackResourceUnavailableException(string operation)
        : base($"{operation} returned no usable playback media.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operation = operation;
    }

    public PlaybackResourceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        Operation = "unknown";
    }

    public string Operation { get; }
}
