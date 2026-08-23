using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
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
    internal const string AuthorizationPipeArgument = "--restart-authorization-pipe";

    private const byte CommitAuthorization = 1;

    private readonly ILogger<ProcessRestartLauncher> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public IProcessRestartTransaction? TryPrepareHelper(int parentProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        try
        {
            return new ProcessRestartTransaction(parentProcessId);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
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
        if (!TryParseRestartRequest(arguments, out var parentProcessId, out var pipeHandle))
        {
            return false;
        }

        using var authorizationPipe = new AnonymousPipeClientStream(PipeDirection.In, pipeHandle);
        await ExecuteAuthorizedRestartAsync(
                authorizationPipe,
                async token =>
                {
                    await WaitForParentExitAsync(parentProcessId, token).ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();
                    using var process = Process.Start(CreateStartInfo(null));
                    if (process == null)
                    {
                        throw new InvalidOperationException("The application could not be relaunched.");
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    internal static async Task<bool> ExecuteAuthorizedRestartAsync(
        Stream authorization,
        Func<CancellationToken, Task> restart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(restart);

        var decision = new byte[1];
        var bytesRead = await authorization
            .ReadAsync(decision.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        if (bytesRead != 1 || decision[0] != CommitAuthorization)
        {
            return false;
        }

        await restart(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task WaitForParentExitAsync(
        int parentProcessId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return;
        }
    }

    internal static bool TryParseRestartRequest(
        IReadOnlyList<string> arguments,
        out int parentProcessId,
        out string pipeHandle)
    {
        parentProcessId = 0;
        pipeHandle = string.Empty;
        return arguments.Count == 4
               && string.Equals(arguments[0], WaitForParentArgument, StringComparison.Ordinal)
               && int.TryParse(
                   arguments[1],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out parentProcessId)
               && parentProcessId > 0
               && string.Equals(arguments[2], AuthorizationPipeArgument, StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(pipeHandle = arguments[3]);
    }

    internal static ProcessStartInfo CreateStartInfo(
        int? parentProcessId,
        string? authorizationPipeHandle = null)
    {
        if (parentProcessId.HasValue != !string.IsNullOrWhiteSpace(authorizationPipeHandle))
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

        public ProcessRestartTransaction(int parentProcessId)
        {
            _authorizationPipe = new AnonymousPipeServerStream(
                PipeDirection.Out,
                HandleInheritability.Inheritable);
            try
            {
                _process = Process.Start(CreateStartInfo(
                    parentProcessId,
                    _authorizationPipe.GetClientHandleAsString()))
                    ?? throw new InvalidOperationException(
                        "The restart helper could not be started.");
                try
                {
                    _authorizationPipe.DisposeLocalCopyOfClientHandle();
                }
                catch
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            _process.Kill(entireProcessTree: true);
                        }
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
            await _authorizationPipe.DisposeAsync().ConfigureAwait(false);
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _process.Dispose();
            }
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
