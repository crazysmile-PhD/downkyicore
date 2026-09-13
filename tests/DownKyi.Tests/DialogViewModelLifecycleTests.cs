using DownKyi.Platform;
using DownKyi.ViewModels.Dialogs;

namespace DownKyi.Tests;

public sealed class DialogViewModelLifecycleTests
{
    [Fact]
    public async Task LifecycleAwaitsCloseBeforeAsyncDisposal()
    {
        var viewModel = new BlockingDialogViewModel();
        await using var viewModelScope = viewModel.ConfigureAwait(true);

        var lifecycleTask = AvaloniaDialogService.CompleteViewModelLifecycleAsync(viewModel);
        await viewModel.CloseStarted.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.False(lifecycleTask.IsCompleted);
        Assert.False(viewModel.IsDisposed);

        viewModel.AllowClose.TrySetResult();
        await lifecycleTask.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(viewModel.IsDisposed);
    }

    [Fact]
    public async Task LifecycleDisposesViewModelWhenAsyncCloseFails()
    {
        var viewModel = new FaultingDialogViewModel();
        await using var viewModelScope = viewModel.ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AvaloniaDialogService.CompleteViewModelLifecycleAsync(viewModel)).ConfigureAwait(true);

        Assert.Equal("Dialog close failed.", exception.Message);
        Assert.True(viewModel.IsDisposed);
    }

    private sealed class BlockingDialogViewModel : BaseDialogViewModel, IAsyncDisposable
    {
        public TaskCompletionSource CloseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowClose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }

        public override async Task OnDialogClosedAsync()
        {
            CloseStarted.TrySetResult();
            await AllowClose.Task.ConfigureAwait(true);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultingDialogViewModel : BaseDialogViewModel, IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public override Task OnDialogClosedAsync()
        {
            return Task.FromException(new InvalidOperationException("Dialog close failed."));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
