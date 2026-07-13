namespace DownKyi.Application.Desktop;

public interface IFilePickerService
{
    Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default);

    Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> SelectVideosAsync(CancellationToken cancellationToken = default);
}
