using Avalonia.Threading;
using DownKyi.ViewModels.Toolbox;

namespace DownKyi.Desktop.Tests;

public sealed class ToolboxOutputBufferTests
{
    [Fact]
    public void BatchOutputHasABoundedTailAndOnePendingPresentation()
    {
        var pending = new Queue<Action>();
        var presented = new List<string>();
        var buffer = new ToolboxOutputBuffer(presented.Add, pending.Enqueue);
        var append = buffer.BeginOperation();
        for (var i = 0; i < 64; i++) append(new string('x', 16384) + $" final-{i}");

        Assert.Single(pending);
        Assert.Equal(string.Empty, Assert.Single(presented));
        pending.Dequeue()();
        Assert.Equal(ToolboxOutputBuffer.MaximumCharacters, presented[^1].Length);
        Assert.EndsWith("final-63\n", presented[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void StartingANewOperationRejectsOldCallbacksAndQueuedText()
    {
        var pending = new Queue<Action>();
        var presented = new List<string>();
        var buffer = new ToolboxOutputBuffer(presented.Add, pending.Enqueue);
        var oldAppend = buffer.BeginOperation();
        oldAppend("old operation");
        var currentAppend = buffer.BeginOperation();
        oldAppend("late failure");
        currentAppend("current operation");
        Assert.Single(pending);
        pending.Dequeue()();
        Assert.Equal("current operation\n", presented[^1]);
    }

    [AvaloniaFact]
    public async Task BackgroundCompletionPublishesOnTheUiThreadAndKeepsTheLastDiagnostic()
    {
        var wrongThread = false;
        var text = string.Empty;
        var buffer = new ToolboxOutputBuffer(value =>
        {
            wrongThread |= !Dispatcher.UIThread.CheckAccess();
            text = value;
        }, action => Dispatcher.UIThread.Post(action));
        var append = buffer.BeginOperation();
        await Task.Run(() => append(new string('x', 2 * 1024 * 1024) + " last error")).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        Assert.False(wrongThread);
        Assert.Equal(ToolboxOutputBuffer.MaximumCharacters, text.Length);
        Assert.EndsWith("last error\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TrimmingDoesNotStartThePreviewWithHalfOfASurrogatePair()
    {
        var pending = new Queue<Action>();
        var text = string.Empty;
        var buffer = new ToolboxOutputBuffer(value => text = value, pending.Enqueue);
        var append = buffer.BeginOperation();
        append(string.Concat(Enumerable.Repeat("😀", ToolboxOutputBuffer.MaximumCharacters)));
        pending.Dequeue()();
        Assert.True(text.Length <= ToolboxOutputBuffer.MaximumCharacters);
        Assert.False(char.IsLowSurrogate(text[0]));
    }
}
