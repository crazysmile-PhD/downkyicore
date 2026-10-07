using Avalonia.Controls;
using Avalonia.Threading;

namespace DownKyi.Desktop.Tests;

public sealed class AvaloniaFactLifecycleTests : IAsyncLifetime
{
    private readonly int _threadId;

    public AvaloniaFactLifecycleTests()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());
        _threadId = Environment.CurrentManagedThreadId;
    }

    public async ValueTask InitializeAsync()
    {
        await Task.Yield();
        AssertDispatcherAccess();
    }

    [AvaloniaFact]
    public async Task AsyncLifecycleRetainsDispatcherAndXunitContext()
    {
        AssertDispatcherAccess();
        var test = TestContext.Current.Test;
        Assert.NotNull(test);
        var control = new TextBlock { Text = "before await" };

        await Task.Yield();

        AssertDispatcherAccess();
        Assert.Same(test, TestContext.Current.Test);
        control.Text = "after await";
        Assert.Equal("after await", control.Text);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        AssertDispatcherAccess();
    }

    private void AssertDispatcherAccess()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());
        Assert.Equal(_threadId, Environment.CurrentManagedThreadId);
    }
}
