using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(DownKyi.Desktop.Tests.DesktopTestApplication))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: Xunit.CollectionBehavior(Xunit.CollectionBehavior.CollectionPerAssembly)]
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
[assembly: Xunit.AssemblyFixture(typeof(DownKyi.Desktop.Tests.AvaloniaTestSessionFixture))]

namespace DownKyi.Desktop.Tests;

internal static class DesktopTestApplication
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<TestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private sealed class TestApplication : Avalonia.Application
    {
    }
}
