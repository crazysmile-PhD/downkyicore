using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using DownKyi.Application.Desktop;

namespace DownKyi.Platform;

internal sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = App.Current.MainWindow.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }
}
