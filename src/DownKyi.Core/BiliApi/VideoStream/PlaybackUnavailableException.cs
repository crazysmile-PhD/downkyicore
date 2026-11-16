namespace DownKyi.Core.BiliApi.VideoStream;

/// <summary>
/// Playback was resolved, but no media can satisfy the requested operation.
/// </summary>
public class PlaybackUnavailableException : InvalidOperationException
{
    public PlaybackUnavailableException()
    {
    }

    public PlaybackUnavailableException(string message)
        : base(message)
    {
    }

    public PlaybackUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
