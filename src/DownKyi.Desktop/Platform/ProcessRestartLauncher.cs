using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Platform;

internal interface IProcessRestartLauncher
{
    IProcessRestartTransaction? TryPrepareHelper(int parentProcessId);
}

internal interface IProcessRestartTransaction : IAsyncDisposable
{
    void Commit();

    Task RevokeAsync();
}

internal sealed class ProcessRestartLauncher(ILogger<ProcessRestartLauncher> logger) : IProcessRestartLauncher
{
    internal const string WaitForParentArgument = "--restart-after-pid";
    internal const string ParentStartedAtArgument = "--restart-parent-started-at-utc-ticks";
    internal const string AuthorizationPipeArgument = "--restart-authorization-pipe";
    internal static readonly TimeSpan RestartHelperTerminationTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan RestartParentExitTimeout = TimeSpan.FromSeconds(30);

    private const byte CommitAuthorization = 1;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The restart transaction owner must attempt every revocation stage and preserve every concurrent failure.")]
    internal static async Task RevokeOwnedHelperAsync(
        Func<ValueTask> closeAuthorization,
        Func<bool> hasExited,
        Action terminate,
        Func<Task> waitForExit,
        Action release,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(closeAuthorization);
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(terminate);
        ArgumentNullException.ThrowIfNull(waitForExit);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var failures = new List<Exception>();
        try
        {
            await closeAuthorization().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            if (!hasExited())
            {
                terminate();
                await waitForExit().WaitAsync(timeout).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            release();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Restart helper revocation encountered multiple failures.",
                failures);
        }
    }

    private readonly ILogger<ProcessRestartLauncher> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public IProcessRestartTransaction? TryPrepareHelper(int parentProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            var parentStartedAtUtcTicks = parent.StartTime.ToUniversalTime().Ticks;
            return new ProcessRestartTransaction(parentProcessId, parentStartedAtUtcTicks);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException or IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            _logger.LogErrorMessage("The restart helper could not be started.", e);
            return null;
        }
    }

    public static async Task<bool> RunHelperIfRequestedAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParseRestartRequest(
                arguments,
                out var parentProcessId,
                out var parentStartedAtUtcTicks,
                out var pipeHandle))
        {
            return false;
        }

        using var parent = CaptureParentProcess(parentProcessId, parentStartedAtUtcTicks);
        using var authorizationPipe = new AnonymousPipeClientStream(PipeDirection.In, pipeHandle);
        await ExecuteAuthorizedRestartAsync(
                authorizationPipe,
                token => parent?.WaitForExitAsync(token) ?? Task.CompletedTask,
                _ =>
                {
                    using var process = Process.Start(CreateStartInfo(null));
                    if (process == null)
                    {
                        throw new InvalidOperationException("The application could not be relaunched.");
                    }

                    return Task.CompletedTask;
                },
                RestartParentExitTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    internal static async Task<bool> ExecuteAuthorizedRestartAsync(
        Stream authorization,
        Func<CancellationToken, Task> waitForParentExit,
        Func<CancellationToken, Task> restart,
        TimeSpan parentExitTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(waitForParentExit);
        ArgumentNullException.ThrowIfNull(restart);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            parentExitTimeout,
            TimeSpan.Zero);

        var decision = new byte[1];
        var bytesRead = await authorization
            .ReadAsync(decision.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        if (bytesRead != 1 || decision[0] != CommitAuthorization)
        {
            return false;
        }

        await waitForParentExit(cancellationToken)
            .WaitAsync(parentExitTimeout, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await restart(cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static Process? CaptureParentProcess(
        int parentProcessId,
        long parentStartedAtUtcTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentStartedAtUtcTicks);
        try
        {
            var parent = Process.GetProcessById(parentProcessId);
            try
            {
                _ = parent.Handle;
                if (parent.StartTime.ToUniversalTime().Ticks != parentStartedAtUtcTicks)
                {
                    throw new InvalidOperationException(
                        "The restart helper parent identity no longer matches its prepared owner.");
                }

                return parent;
            }
            catch
            {
                parent.Dispose();
                throw;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static bool TryParseRestartRequest(
        IReadOnlyList<string> arguments,
        out int parentProcessId,
        out long parentStartedAtUtcTicks,
        out string pipeHandle)
    {
        parentProcessId = 0;
        parentStartedAtUtcTicks = 0;
        pipeHandle = string.Empty;
        return arguments.Count == 6
               && string.Equals(arguments[0], WaitForParentArgument, StringComparison.Ordinal)
               && int.TryParse(
                   arguments[1],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                    out parentProcessId)
               && parentProcessId > 0
               && string.Equals(arguments[2], ParentStartedAtArgument, StringComparison.Ordinal)
               && long.TryParse(
                   arguments[3],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out parentStartedAtUtcTicks)
               && parentStartedAtUtcTicks > 0
               && parentStartedAtUtcTicks <= DateTime.MaxValue.Ticks
               && string.Equals(arguments[4], AuthorizationPipeArgument, StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(pipeHandle = arguments[5]);
    }

    internal static ProcessStartInfo CreateStartInfo(
        int? parentProcessId,
        long? parentStartedAtUtcTicks = null,
        string? authorizationPipeHandle = null)
    {
        if (parentProcessId.HasValue != parentStartedAtUtcTicks.HasValue ||
            parentProcessId.HasValue != !string.IsNullOrWhiteSpace(authorizationPipeHandle))
        {
            throw new ArgumentException(
                "Restart helper process and authorization pipe arguments must be provided together.");
        }

        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var isDotnetHost = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (isDotnetHost)
        {
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                throw new InvalidOperationException("The managed application entry point is unavailable.");
            }

            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        if (parentProcessId is { } processId)
        {
            startInfo.ArgumentList.Add(WaitForParentArgument);
            startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(ParentStartedAtArgument);
            startInfo.ArgumentList.Add(
                parentStartedAtUtcTicks!.Value.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(AuthorizationPipeArgument);
            startInfo.ArgumentList.Add(authorizationPipeHandle!);
        }

        return startInfo;
    }

    private sealed class ProcessRestartTransaction : IProcessRestartTransaction
    {
        private readonly AnonymousPipeServerStream _authorizationPipe;
        private readonly Process _process;
        private int _completionState;

        public ProcessRestartTransaction(int parentProcessId, long parentStartedAtUtcTicks)
        {
            _authorizationPipe = new AnonymousPipeServerStream(
                PipeDirection.Out,
                HandleInheritability.Inheritable);
            try
            {
                _process = Process.Start(CreateStartInfo(
                    parentProcessId,
                    parentStartedAtUtcTicks,
                    _authorizationPipe.GetClientHandleAsString()))
                    ?? throw new InvalidOperationException(
                        "The restart helper could not be started.");
                try
                {
                    _authorizationPipe.DisposeLocalCopyOfClientHandle();
                }
                catch (Exception initializationFailure)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            _process.Kill(entireProcessTree: true);
                            if (!_process.WaitForExit(
                                    (int)RestartHelperTerminationTimeout.TotalMilliseconds))
                            {
                                throw new TimeoutException(
                                    "The restart helper did not terminate within its owned deadline.");
                            }
                        }
                    }
                    catch (Exception terminationFailure)
                    {
                        throw new AggregateException(
                            "Restart helper initialization failed and its owned process did not terminate cleanly.",
                            initializationFailure,
                            terminationFailure);
                    }
                    finally
                    {
                        _process.Dispose();
                    }

                    throw;
                }
            }
            catch
            {
                _authorizationPipe.Dispose();
                throw;
            }
        }

        public void Commit()
        {
            CompleteTransaction(1);
            try
            {
                _authorizationPipe.WriteByte(CommitAuthorization);
                _authorizationPipe.Flush();
            }
            finally
            {
                _authorizationPipe.Dispose();
                _process.Dispose();
            }
        }

        public async Task RevokeAsync()
        {
            CompleteTransaction(2);
            await RevokeOwnedHelperAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _completionState, 3, comparand: 0) == 0)
            {
                await RevokeOwnedHelperAsync().ConfigureAwait(false);
            }
        }

        private async Task RevokeOwnedHelperAsync()
        {
            await ProcessRestartLauncher.RevokeOwnedHelperAsync(
                    () => _authorizationPipe.DisposeAsync(),
                    () => _process.HasExited,
                    () => _process.Kill(entireProcessTree: true),
                    () => _process.WaitForExitAsync(CancellationToken.None),
                    () => _process.Dispose(),
                    RestartHelperTerminationTimeout)
                .ConfigureAwait(false);
        }

        private void CompleteTransaction(int completionState)
        {
            if (Interlocked.CompareExchange(
                    ref _completionState,
                    completionState,
                    comparand: 0) != 0)
            {
                throw new InvalidOperationException(
                    "The restart helper transaction has already completed.");
            }
        }
    }
}
