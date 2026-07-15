using System;

namespace DownKyi.Platform;

internal sealed class ClipboardTextChangedEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

internal interface IClipboardMonitor : IDisposable
{
    event EventHandler<ClipboardTextChangedEventArgs>? Changed;
}
