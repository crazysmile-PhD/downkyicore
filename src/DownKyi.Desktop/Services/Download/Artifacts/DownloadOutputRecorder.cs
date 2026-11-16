using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal static class DownloadOutputRecorder
{
    public static async Task<string> RecordFileSizeAsync(
        DownloadTaskId taskId,
        string? filePath,
        DownloadTaskStateWriter stateWriter,
        CancellationToken cancellationToken)
    {
        var fileSize = File.Exists(filePath)
            ? Format.FormatFileSize(new FileInfo(filePath).Length)
            : Format.FormatFileSize(0);
        await stateWriter.UpdateOutputFileSizeAsync(taskId, fileSize, cancellationToken)
            .ConfigureAwait(true);
        return fileSize;
    }
}
