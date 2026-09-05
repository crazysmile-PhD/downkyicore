using System.Diagnostics;

namespace DownKyi.Tests;

internal interface IAria2TlsProcessHandle : IDisposable
{
    bool HasExited { get; }

    void KillEntireTree();

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class Aria2TlsProcessLifetime : IAsyncDisposable
{
    private readonly TimeSpan _cleanupTimeout;
    private readonly Func<Task> _forceShutdown;
    private readonly CancellationTokenSource _outputCaptureCancellation;
    private readonly IAria2TlsProcessHandle _process;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Task<string> _standardError;
    private readonly Task<string> _standardOutput;
    private int _cleanupStarted;
    private Task? _forceShutdownTask;
    private bool _forceShutdownTimedOut;

    internal Aria2TlsProcessLifetime(
        IAria2TlsProcessHandle process,
        Task<string> standardOutput,
        Task<string> standardError,
        CancellationTokenSource outputCaptureCancellation,
        Func<Task> forceShutdown,
        TimeSpan shutdownTimeout,
        TimeSpan cleanupTimeout)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _standardOutput = standardOutput ?? throw new ArgumentNullException(nameof(standardOutput));
        _standardError = standardError ?? throw new ArgumentNullException(nameof(standardError));
        _outputCaptureCancellation = outputCaptureCancellation
            ?? throw new ArgumentNullException(nameof(outputCaptureCancellation));
        _forceShutdown = forceShutdown ?? throw new ArgumentNullException(nameof(forceShutdown));
        _shutdownTimeout = shutdownTimeout;
        _cleanupTimeout = cleanupTimeout;
    }

    public static async Task<Aria2TlsProcessLifetime> CreateAsync(
        Process process,
        Func<Task> forceShutdown,
        TimeSpan shutdownTimeout,
        TimeSpan cleanupTimeout)
    {
        ArgumentNullException.ThrowIfNull(process);
        var outputCancellation = new CancellationTokenSource();
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        var initializationFailure = Record.Exception(() =>
        {
            standardOutput = process.StandardOutput.ReadToEndAsync(outputCancellation.Token);
            standardError = process.StandardError.ReadToEndAsync(outputCancellation.Token);
        });
        if (initializationFailure == null)
        {
            return new Aria2TlsProcessLifetime(
                new SystemAria2TlsProcessHandle(process),
                standardOutput!,
                standardError!,
                outputCancellation,
                forceShutdown,
                shutdownTimeout,
                cleanupTimeout);
        }

        var failedLifetime = new Aria2TlsProcessLifetime(
            new SystemAria2TlsProcessHandle(process),
            standardOutput ?? Task.FromResult(string.Empty),
            standardError ?? Task.FromResult(string.Empty),
            outputCancellation,
            forceShutdown,
            shutdownTimeout,
            cleanupTimeout);
        var failures = new Aria2TlsFailureCollector();
        failures.Capture("runtime-startup/output-capture", initializationFailure);
        await failedLifetime.CleanupAsync(failures).ConfigureAwait(false);
        failures.ThrowIfAny();
        throw new InvalidOperationException("Unreachable output-capture startup failure path.");
    }

    internal async Task CleanupAsync(Aria2TlsFailureCollector failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
        {
            return;
        }

        var hasExited = TryHasExited(failures);
        if (!hasExited)
        {
            await failures.RunAsync(
                "runtime-disposal",
                ForceShutdownWithinDeadlineAsync).ConfigureAwait(false);
            hasExited = await WaitForExitAsync(
                _shutdownTimeout,
                "process-wait",
                recordTimeout: false,
                failures).ConfigureAwait(false);
        }

        if (!hasExited)
        {
            if (!TryHasExited(failures))
            {
                failures.Run("process-kill", _process.KillEntireTree);
            }

            await WaitForExitAsync(
                _cleanupTimeout,
                "process-reap",
                recordTimeout: true,
                failures).ConfigureAwait(false);
        }

        if (_forceShutdownTimedOut)
        {
            await ObserveTimedOutShutdownAsync(failures).ConfigureAwait(false);
        }

        await DrainOutputAsync(failures).ConfigureAwait(false);
        failures.Run("process-dispose", _process.Dispose);
        await failures.RunAsync(
            "output-cancellation",
            () => _outputCaptureCancellation.CancelAsync()).ConfigureAwait(false);
        failures.Run("output-cancellation-dispose", _outputCaptureCancellation.Dispose);
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new Aria2TlsFailureCollector();
        await CleanupAsync(failures).ConfigureAwait(false);
        failures.ThrowIfAny();
    }

    private async Task ForceShutdownWithinDeadlineAsync()
    {
        using var deadline = new CancellationTokenSource(_shutdownTimeout);
        _forceShutdownTask = _forceShutdown();
        try
        {
            await _forceShutdownTask.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            _forceShutdownTimedOut = true;
            throw new TimeoutException(
                "The aria2 RPC shutdown call did not complete before its cleanup deadline.",
                exception);
        }
    }

    private async Task ObserveTimedOutShutdownAsync(Aria2TlsFailureCollector failures)
    {
        if (_forceShutdownTask == null)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(_cleanupTimeout);
        var exception = await Record.ExceptionAsync(
            () => _forceShutdownTask.WaitAsync(deadline.Token)).ConfigureAwait(false);
        if (exception is OperationCanceledException && deadline.IsCancellationRequested)
        {
            ObserveLateFault(_forceShutdownTask);
            return;
        }

        if (exception != null)
        {
            failures.Capture("runtime-disposal/late-completion", exception);
        }
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private bool TryHasExited(Aria2TlsFailureCollector failures)
    {
        var hasExited = false;
        var exception = Record.Exception(() => hasExited = _process.HasExited);
        if (exception != null)
        {
            failures.Capture("process-state", exception);
            return false;
        }

        return hasExited;
    }

    private async Task<bool> WaitForExitAsync(
        TimeSpan timeout,
        string failureStage,
        bool recordTimeout,
        Aria2TlsFailureCollector failures)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var exception = await Record.ExceptionAsync(
            () => _process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
        if (exception == null)
        {
            return true;
        }

        if (exception is OperationCanceledException && deadline.IsCancellationRequested)
        {
            if (recordTimeout)
            {
                failures.Capture(
                    failureStage,
                    new TimeoutException($"aria2 did not exit within the {failureStage} deadline."));
            }

            return false;
        }

        failures.Capture(failureStage, exception);
        return false;
    }

    private async Task DrainOutputAsync(Aria2TlsFailureCollector failures)
    {
        using var deadline = new CancellationTokenSource(_cleanupTimeout);
        var standardOutput = ObserveOutputAsync(
            _standardOutput,
            "stdout-drain",
            deadline.Token);
        var standardError = ObserveOutputAsync(
            _standardError,
            "stderr-drain",
            deadline.Token);
        var observations = await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        foreach (var observation in observations)
        {
            if (observation.Exception != null)
            {
                failures.Capture(observation.Stage, observation.Exception);
            }
        }
    }

    private static async Task<OutputObservation> ObserveOutputAsync(
        Task<string> output,
        string stage,
        CancellationToken cancellationToken)
    {
        var exception = await Record.ExceptionAsync(
            () => output.WaitAsync(cancellationToken)).ConfigureAwait(false);
        if (exception == null)
        {
            return new OutputObservation(stage, null);
        }

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return new OutputObservation(
                stage,
                new TimeoutException($"{stage} did not complete before the cleanup deadline."));
        }

        return new OutputObservation(stage, exception);
    }

    private sealed record OutputObservation(string Stage, Exception? Exception);

    private sealed class SystemAria2TlsProcessHandle(Process process) : IAria2TlsProcessHandle
    {
        public bool HasExited => process.HasExited;

        public void KillEntireTree()
        {
            process.Kill(entireProcessTree: true);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return process.WaitForExitAsync(cancellationToken);
        }

        public void Dispose()
        {
            process.Dispose();
        }
    }
}
