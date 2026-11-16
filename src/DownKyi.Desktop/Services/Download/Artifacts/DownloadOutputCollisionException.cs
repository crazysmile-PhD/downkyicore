using System;
using System.IO;

namespace DownKyi.Services.Download;

internal sealed class DownloadOutputCollisionException : IOException
{
    public DownloadOutputCollisionException()
        : base("The selected output path is already in use.")
    {
    }

    public DownloadOutputCollisionException(string message)
        : base(message)
    {
    }

    public DownloadOutputCollisionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
