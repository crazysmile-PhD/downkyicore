namespace DownKyi.Services.Download;

/// <summary>
/// Shares one output directory across the pages of one finalized download.
/// The selected directory is committed only after the first task is persisted.
/// </summary>
internal sealed class DownloadOutputPackage(string directory)
{
    public string Directory { get; } = directory;

    public string? AdmittedDirectory { get; set; }
}
