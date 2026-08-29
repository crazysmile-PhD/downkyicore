using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;

[assembly: Xunit.AssemblyFixture(
    typeof(DownKyi.MacOS.Tests.MacProcessGroupDiagnosticsFixture))]

namespace DownKyi.MacOS.Tests;

[SuppressMessage(
    "Maintainability",
    "CA1515:Consider making public types internal",
    Justification = "xUnit constructs the public assembly fixture from its assembly-level registration.")]
public sealed class MacProcessGroupDiagnosticsFixture : IAsyncDisposable
{
    private static readonly ConcurrentQueue<CompilerServerEvidence> CompilerServerEvidenceQueue = new();
    private readonly int? _processGroupId;
    private readonly ProcessIdentity[] _initialUnexpected = [];
    private readonly string? _initialDiagnosticFailure;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The baseline snapshot is diagnostic evidence and cannot replace the outer lease verdict.")]
    public MacProcessGroupDiagnosticsFixture()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            _processGroupId = MacNative.GetProcessGroup();
            _initialUnexpected = DescribeUnexpectedMembers(_processGroupId.Value);
        }
        catch (Exception failure)
        {
            _initialDiagnosticFailure = failure.GetType().Name;
        }
    }

    internal static void RecordCompilerServerEvidence(
        int invocationProcessId,
        int? clientProcessId,
        int? serverProcessId,
        string? serverProcessName,
        bool serverAliveAfterInvocation,
        int? keepAliveMilliseconds,
        string? diagnosticFailure)
    {
        CompilerServerEvidenceQueue.Enqueue(new CompilerServerEvidence(
            invocationProcessId,
            clientProcessId,
            serverProcessId,
            serverProcessName,
            serverAliveAfterInvocation,
            keepAliveMilliseconds,
            diagnosticFailure));
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A diagnostics-only observer cannot replace the causal test or outer lease failure.")]
    public async ValueTask DisposeAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var processGroupId = _processGroupId ?? MacNative.GetProcessGroup();
            var unexpected = DescribeUnexpectedMembers(processGroupId);
            var compilerServerEvidence = CompilerServerEvidenceQueue.ToArray();
            if (unexpected.Length > 0 ||
                compilerServerEvidence.Length > 0 ||
                _initialDiagnosticFailure != null)
            {
                await Console.Error.WriteLineAsync(
                    "[DownKyi.MacOS.Tests process-group observer] " +
                    JsonSerializer.Serialize(new
                    {
                        processGroupId,
                        testProcessId = Environment.ProcessId,
                        initialUnexpected = _initialUnexpected,
                        initialDiagnosticFailure = _initialDiagnosticFailure,
                        compilerServerEvidence,
                        unexpected
                    })).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            await Console.Error.WriteLineAsync(
                "[DownKyi.MacOS.Tests process-group observer unavailable] " +
                failure.GetType().Name).ConfigureAwait(false);
        }
    }

    private static ProcessIdentity[] DescribeUnexpectedMembers(int processGroupId)
    {
        return QueryMembers(processGroupId)
            .Where(processId =>
                processId != processGroupId &&
                processId != Environment.ProcessId)
            .Select(processId => new ProcessIdentity(processId, GetProcessName(processId)))
            .OrderBy(process => process.ProcessId)
            .ToArray();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A disappearing diagnostic process is reported as unavailable, not a correctness result.")]
    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }

    private static HashSet<int> QueryMembers(int processGroupId)
    {
        Marshal.SetLastPInvokeError(0);
        var suggestedCapacity = MacNative.ListProcessGroupPids(processGroupId, null, 0);
        var initialError = Marshal.GetLastPInvokeError();
        if (suggestedCapacity == 0 && initialError != 0)
        {
            throw new InvalidOperationException(
                $"The macOS process-group observer is unavailable: {initialError}.");
        }
        if (suggestedCapacity < 32)
        {
            suggestedCapacity = 32;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var capacity = checked(suggestedCapacity << attempt);
            var processIds = new int[capacity];
            Marshal.SetLastPInvokeError(0);
            var count = MacNative.ListProcessGroupPids(
                processGroupId,
                processIds,
                checked(capacity * sizeof(int)));
            var queryError = Marshal.GetLastPInvokeError();
            if (count == 0 && queryError != 0)
            {
                throw new InvalidOperationException(
                    $"The macOS process-group observer failed: {queryError}.");
            }
            if (count < 0)
            {
                throw new InvalidOperationException(
                    "The macOS process-group observer returned an invalid count.");
            }
            if (count >= capacity)
            {
                continue;
            }

            return processIds[..count].ToHashSet();
        }

        throw new InvalidOperationException(
            "The macOS process-group observer did not converge.");
    }

    private sealed record ProcessIdentity(int ProcessId, string ProcessName);

    private sealed record CompilerServerEvidence(
        int InvocationProcessId,
        int? ClientProcessId,
        int? ServerProcessId,
        string? ServerProcessName,
        bool ServerAliveAfterInvocation,
        int? KeepAliveMilliseconds,
        string? DiagnosticFailure);

    [SuppressMessage(
        "Interoperability",
        "CA1401:P/Invokes should not be visible",
        Justification = "The diagnostics-only native boundary is private to this macOS fixture.")]
    private static class MacNative
    {
        [DllImport(
            "/usr/lib/libSystem.B.dylib",
            EntryPoint = "getpgrp",
            SetLastError = true)]
        internal static extern int GetProcessGroup();

        [DllImport(
            "/usr/lib/libproc.dylib",
            EntryPoint = "proc_listpgrppids",
            SetLastError = true)]
        internal static extern int ListProcessGroupPids(
            int processGroupId,
            [Out] int[]? processIds,
            int bufferSize);
    }
}
