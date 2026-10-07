using System.Diagnostics.CodeAnalysis;
using Avalonia.Headless;

namespace DownKyi.Desktop.Tests;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "xUnit instantiates and disposes this fixture through the assembly's AssemblyFixture registration.")]
internal sealed class AvaloniaTestSessionFixture : IAsyncDisposable
{
    public AvaloniaTestSessionFixture()
    {
    }

    internal HeadlessUnitTestSession Session { get; } =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DesktopTestApplication).Assembly);

    public ValueTask DisposeAsync()
    {
        return Session.DisposeAsync();
    }
}
