namespace DownKyi.Application.Downloads;

public interface IDownloadStoreRecovery
{
    Task BackupAndResetAsync(CancellationToken cancellationToken);
}
