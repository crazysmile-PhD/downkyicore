namespace DownKyi.Application.Desktop;

public interface IPlatformLauncher
{
    Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default);
}
