using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.ProcessSupervision;
using Microsoft.Extensions.Logging;

namespace DownKyi.Platform;

internal interface IProcessRestartLauncher
{
    Task<IProcessRestartTransaction?> TryPrepareHelperAsync(
        CancellationToken cancellationToken = default);
}

internal interface IProcessRestartTransaction : IAsyncDisposable
{
    RestartHandoffState State { get; }

    void Commit();

    Task RevokeAsync();
}

internal sealed class ProcessRestartLauncher(ILogger<ProcessRestartLauncher> logger) :
    IProcessRestartLauncher
{
    internal static readonly TimeSpan RestartHelperTerminationTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan RestartParentExitTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<ProcessRestartLauncher> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Preparation failure must leave the current desktop running and is logged with typed handoff evidence.")]
    public async Task<IProcessRestartTransaction?> TryPrepareHelperAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = TransitionBudget.Start(
            RestartParentExitTimeout,
            RestartHelperTerminationTimeout);
        try
        {
            var lease = await RestartHandoffLease.PrepareAsync(
                    CreateStartInfo(),
                    Environment.ProcessId,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ProcessRestartTransaction(lease);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (failure is RestartHandoffException or IOException
            or Win32Exception or PlatformNotSupportedException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            _logger.LogErrorMessage("The restart helper could not be prepared.", failure);
            return null;
        }
    }

    public static async Task<bool> RunHelperIfRequestedAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var parseResult = RestartHandoffProtocol.ParseRequest(arguments, out var request);
        if (parseResult == RestartHandoffRequestParseResult.NotRequested)
        {
            return false;
        }

        if (parseResult != RestartHandoffRequestParseResult.Valid || request == null)
        {
            throw new RestartHandoffException(new RestartHandoffFailure(
                RestartHandoffFailureKind.AuthorizationRejected,
                RestartHandoffState.Prepared,
                null,
                Environment.ProcessId,
                "The restart helper command line was malformed."));
        }

        var outcome = await RestartHandoffHelper.ExecuteAsync(
                request,
                CreateStartInfo(),
                cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.Succeeded && outcome.Failure != null)
        {
            throw new RestartHandoffException(outcome.Failure);
        }

        return true;
    }

    internal static RestartHandoffRequestParseResult TryParseRestartRequest(
        IReadOnlyList<string> arguments,
        out RestartHandoffRequest? request)
    {
        return RestartHandoffProtocol.ParseRequest(arguments, out request);
    }

    internal static ProcessStartInfo CreateStartInfo()
    {
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
                throw new InvalidOperationException(
                    "The managed application entry point is unavailable.");
            }

            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        return startInfo;
    }

    private sealed class ProcessRestartTransaction(RestartHandoffLease lease) :
        IProcessRestartTransaction
    {
        private readonly RestartHandoffLease _lease = lease
            ?? throw new ArgumentNullException(nameof(lease));

        public RestartHandoffState State => _lease.State;

        public void Commit()
        {
            _lease.Commit();
        }

        public Task RevokeAsync()
        {
            return _lease.RevokeAsync();
        }

        public ValueTask DisposeAsync()
        {
            return _lease.DisposeAsync();
        }
    }
}
