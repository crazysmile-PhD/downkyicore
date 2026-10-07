using System.Runtime.CompilerServices;
using Xunit.v3;

namespace DownKyi.Desktop.Tests;

// Avalonia.Headless.XUnit 12.1.3 uses the xUnit 3.x extension ABI (AvaloniaUI/Avalonia#22072).
// Keep the official headless session and delegate discovery/execution to xUnit 4.x.
[AttributeUsage(AttributeTargets.Method)]
[XunitTestCaseDiscoverer(typeof(AvaloniaFactDiscoverer))]
internal sealed class AvaloniaFactAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : FactAttribute(sourceFilePath, sourceLineNumber);
