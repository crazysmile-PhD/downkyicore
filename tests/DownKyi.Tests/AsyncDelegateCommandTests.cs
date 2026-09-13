using System.Collections.Concurrent;
using DownKyi.Commands;
using Microsoft.Extensions.Logging;

namespace DownKyi.Tests;

public sealed class AsyncDelegateCommandTests
{
    [Fact]
    public async Task OperationalFailureIsLoggedAndCommandBecomesExecutableAgain()
    {
        var logger = new RecordingLogger();
        var command = new DownKyiAsyncDelegateCommand(
            () => Task.FromException(new InvalidOperationException("command failed")),
            logger);
        var completion = ObserveCompletion(command);

        command.Execute(null);
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(command.CanExecute(null));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("UI command execution failed.", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task CancellationIsNotLoggedAsAnOperationalFailure()
    {
        var logger = new RecordingLogger();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var command = new DownKyiAsyncDelegateCommand(
            () => Task.FromException(new OperationCanceledException(cancellation.Token)),
            logger);
        var completion = ObserveCompletion(command);

        command.Execute(null);
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(command.CanExecute(null));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task UnexpectedCancellationIsLoggedAsAnOperationalFailure()
    {
        var logger = new RecordingLogger();
        var command = new DownKyiAsyncDelegateCommand(
            () => Task.FromException(new OperationCanceledException()),
            logger);
        var completion = ObserveCompletion(command);

        command.Execute(null);
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(command.CanExecute(null));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("UI command was canceled unexpectedly.", entry.Message);
        Assert.IsType<OperationCanceledException>(entry.Exception);
    }

    [Fact]
    public void ExternalDependencyInvalidationRaisesCanExecuteChanged()
    {
        var isEnabled = false;
        var command = new DownKyiAsyncDelegateCommand(
            () => Task.CompletedTask,
            new RecordingLogger(),
            () => isEnabled);
        var invalidationCount = 0;
        command.CanExecuteChanged += (_, _) => invalidationCount++;

        Assert.False(command.CanExecute(null));

        isEnabled = true;
        command.NotifyCanExecuteChanged();

        Assert.Equal(1, invalidationCount);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ExternalInvalidationDoesNotPermitConcurrentExecution()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var command = new DownKyiAsyncDelegateCommand(
            async () =>
            {
                Interlocked.Increment(ref executionCount);
                started.TrySetResult();
                await release.Task.ConfigureAwait(true);
            },
            new RecordingLogger());
        var completion = ObserveCompletion(command);

        command.Execute(null);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        command.NotifyCanExecuteChanged();
        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(1, Volatile.Read(ref executionCount));

        release.TrySetResult();
        await completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(command.CanExecute(null));
        Assert.Equal(1, Volatile.Read(ref executionCount));
    }

    private static Task ObserveCompletion(DownKyiAsyncDelegateCommand command)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedBusyState = false;
        command.CanExecuteChanged += (_, _) =>
        {
            if (!command.CanExecute(null))
            {
                observedBusyState = true;
            }
            else if (observedBusyState)
            {
                completion.TrySetResult();
            }
        };

        return completion.Task;
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
