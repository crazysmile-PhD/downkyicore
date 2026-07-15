using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using DownKyi.Application.Desktop;

namespace DownKyi.Platform;

internal sealed class AvaloniaClipboardService : IClipboardService
{
    private readonly AvaloniaDesktopContext _desktopContext;

    public AvaloniaClipboardService(AvaloniaDesktopContext desktopContext)
    {
        _desktopContext = desktopContext ?? throw new System.ArgumentNullException(nameof(desktopContext));
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = _desktopContext.MainWindow.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }
}
