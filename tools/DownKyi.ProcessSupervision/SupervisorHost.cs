using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;

namespace DownKyi.ProcessSupervision;

internal static class SupervisorHost
{
    internal const string HostArgument = "--owned-process-host";
    internal const string OwnershipProbeArgument = "--ownership-probe";
    internal const string LaunchSpecProbeArgument = "--launch-spec-probe";
    internal const string OwnedTreeProbeArgument = "--owned-tree-probe";
    internal const string ExitWithOwnedDescendantArgument = "--exit-with-owned-descendant";
    internal const string BlockForeverArgument = "--block-forever";
    internal const string CollectorBlockWithReadyArgument =
        "--collector-block-with-ready";
    internal const string CollectorPublishBeforeBlockArgument =
        "--collector-publish-before-block";
    internal const string CollectorOutputArgument = "--collector-output";
    internal const string CollectorNonzeroArgument = "--collector-nonzero";
    internal const string CollectorLargeOutputArgument = "--collector-large-output";
    internal const string EvidenceHoldProbeArgument = "--evidence-hold-probe";
    internal const string EvidenceHoldWithOwnedDescendantArgument =
        "--evidence-hold-with-owned-descendant";
    internal const string IgnoreEvidenceHoldArgument = "--ignore-evidence-hold";

    private const string EvidenceHoldEnvironmentVariable =
        "DOWNKYI_FORENSICS_CAPTURE_PIPE";
    private const byte EvidenceCaptureCompleted = 0xA5;
    private const byte EvidenceCaptureAcknowledged = 0x5A;

    private const byte OwnershipAttachment = 0xB1;
    private const byte LaunchAuthorization = 0xC1;
    private const byte OwnershipEstablished = 0xA1;
    private const byte OwnershipMutationActive = 0xA2;
    private const byte TargetStarted = 0xA3;
    private const byte TargetExited = 0xA4;
    private const byte FinalizeSupervisor = 0xD1;
    private const int MaximumLaunchPayloadBytes = 1024 * 1024;

    [SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using disposable objects complete before disposing",
        Justification = "The owner-death transition atomically terminates this process; all non-terminating protocol paths await both process and pipe tasks.")]
    public static async Task<int?> RunIfRequestedAsync(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], OwnershipProbeArgument, StringComparison.Ordinal))
        {
            return RunOwnershipProbe();
        }
        if (arguments.Count == 2 &&
            string.Equals(arguments[0], LaunchSpecProbeArgument, StringComparison.Ordinal))
        {
            return RunLaunchSpecProbe(arguments[1]);
        }
        if (arguments.Count == 2 &&
            string.Equals(arguments[0], OwnedTreeProbeArgument, StringComparison.Ordinal))
        {
            return await RunOwnedTreeProbeAsync(arguments[1]).ConfigureAwait(false);
        }
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], BlockForeverArgument, StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None)
                .ConfigureAwait(false);
            return 0;
        }
        if (arguments.Count == 2 &&
            (string.Equals(
                    arguments[0],
                    CollectorBlockWithReadyArgument,
                    StringComparison.Ordinal) ||
                string.Equals(
                    arguments[0],
                    CollectorPublishBeforeBlockArgument,
                    StringComparison.Ordinal)))
        {
            return await RunCollectorBlockProbeAsync(
                    arguments[1],
                    publishBeforeBlock: string.Equals(
                        arguments[0],
                        CollectorPublishBeforeBlockArgument,
                        StringComparison.Ordinal))
                .ConfigureAwait(false);
        }
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], CollectorOutputArgument, StringComparison.Ordinal))
        {
            return await RunCollectorOutputProbeAsync(exitCode: 0, largeOutput: false)
                .ConfigureAwait(false);
        }
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], CollectorNonzeroArgument, StringComparison.Ordinal))
        {
            return await RunCollectorOutputProbeAsync(exitCode: 23, largeOutput: false)
                .ConfigureAwait(false);
        }
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], CollectorLargeOutputArgument, StringComparison.Ordinal))
        {
            return await RunCollectorOutputProbeAsync(exitCode: 0, largeOutput: true)
                .ConfigureAwait(false);
        }
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], EvidenceHoldProbeArgument, StringComparison.Ordinal))
        {
            return await RunEvidenceHoldProbeAsync(withOwnedDescendant: false)
                .ConfigureAwait(false);
        }
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], IgnoreEvidenceHoldArgument, StringComparison.Ordinal))
        {
            Environment.SetEnvironmentVariable(EvidenceHoldEnvironmentVariable, null);
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None)
                .ConfigureAwait(false);
            return 0;
        }
        if (arguments.Count == 1 &&
            string.Equals(
                arguments[0],
                EvidenceHoldWithOwnedDescendantArgument,
                StringComparison.Ordinal))
        {
            return await RunEvidenceHoldProbeAsync(withOwnedDescendant: true)
                .ConfigureAwait(false);
        }
        if (arguments.Count == 2 &&
            string.Equals(arguments[0], ExitWithOwnedDescendantArgument, StringComparison.Ordinal))
        {
            return await RunExitWithOwnedDescendantProbeAsync(arguments[1]).ConfigureAwait(false);
        }

        const ProcessOwnershipMutation supportedMutations =
            ProcessOwnershipMutation.ResumeTargetBeforeOwnership |
            ProcessOwnershipMutation.FailAfterContainmentTermination |
            ProcessOwnershipMutation.FailAfterRootReap |
            ProcessOwnershipMutation.FailOwnershipEstablishment |
            ProcessOwnershipMutation.FailMembershipQuery |
            ProcessOwnershipMutation.StallLaunchPayloadRead |
            ProcessOwnershipMutation.DelayAfterTargetExitReport |
            ProcessOwnershipMutation.ReleaseAnchorBeforeMembership |
            ProcessOwnershipMutation.FailFixturePublication |
            ProcessOwnershipMutation.StallStreamDrain |
            ProcessOwnershipMutation.StallRootReap;
        if (arguments.Count != 5 ||
            !string.Equals(arguments[0], HostArgument, StringComparison.Ordinal) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var mutationValue) ||
            (((ProcessOwnershipMutation)mutationValue) & ~supportedMutations) != 0)
        {
            return null;
        }

        var control = new NamedPipeClientStream(
            ".",
            arguments[1],
            PipeDirection.In,
            PipeOptions.Asynchronous);
        await using var controlScope = control.ConfigureAwait(false);
        var status = new NamedPipeClientStream(
            ".",
            arguments[2],
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await using var statusScope = status.ConfigureAwait(false);
        await Task.WhenAll(
                control.ConnectAsync(CancellationToken.None),
                status.ConnectAsync(CancellationToken.None))
            .ConfigureAwait(false);

        var mutation = (ProcessOwnershipMutation)mutationValue;
        var attachmentBytes = await ReadFrameAsync(
                control,
                OwnershipAttachment,
                MaximumLaunchPayloadBytes)
            .ConfigureAwait(false);
        var attachment = JsonSerializer.Deserialize<OwnershipAttachmentPayload>(attachmentBytes)
            ?? throw new InvalidOperationException("The ownership attachment is invalid.");
        var ownershipEstablished = PlatformProcessContainment.EstablishCurrentProcessOwnership(
            attachment.ContainmentId,
            attachment.MembershipId,
            mutation);
        if (!ownershipEstablished &&
            !mutation.HasFlag(ProcessOwnershipMutation.ResumeTargetBeforeOwnership))
        {
            await WriteStatusAsync(status, 0).ConfigureAwait(false);
            return 204;
        }

        await WriteStatusAsync(
                status,
                ownershipEstablished ? OwnershipEstablished : OwnershipMutationActive)
            .ConfigureAwait(false);
        if (mutation.HasFlag(ProcessOwnershipMutation.StallLaunchPayloadRead))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None)
                .ConfigureAwait(false);
        }

        var payloadBytes = await ReadFrameAsync(
                control,
                LaunchAuthorization,
                MaximumLaunchPayloadBytes)
            .ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize<LaunchSpecPayload>(payloadBytes)
            ?? throw new InvalidOperationException("The immutable launch specification is invalid.");
        using var evidenceHoldHandles = payload.EvidenceHold == null
            ? null
            : new EvidenceHoldClientHandleScope(payload.EvidenceHold);
        var startInfo = CreateTargetStartInfo(payload, attachment, mutation);
        evidenceHoldHandles?.ApplyTo(startInfo);
        using var target = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The owned target process did not start.");
        using var supervisorStandardOutput = Console.OpenStandardOutput();
        using var supervisorStandardError = Console.OpenStandardError();
        var targetStandardOutput = target.StandardOutput.BaseStream.CopyToAsync(
            supervisorStandardOutput,
            CancellationToken.None);
        var targetStandardError = target.StandardError.BaseStream.CopyToAsync(
            supervisorStandardError,
            CancellationToken.None);
        evidenceHoldHandles?.ReleaseSupervisorCopies();
        if (payload.CloseStandardInput)
        {
            target.StandardInput.Close();
        }

        var targetStarted = new byte[sizeof(byte) + sizeof(int)];
        targetStarted[0] = TargetStarted;
        BinaryPrimitives.WriteInt32LittleEndian(targetStarted.AsSpan(sizeof(byte)), target.Id);
        await WriteStatusAsync(status, targetStarted).ConfigureAwait(false);

        var targetExit = target.WaitForExitAsync(CancellationToken.None);
        var ownerLifetime = ReadOwnerLifetimeSignalAsync(control);
        var completed = await Task.WhenAny(targetExit, ownerLifetime).ConfigureAwait(false);
        if (completed == ownerLifetime && !targetExit.IsCompleted)
        {
            PlatformProcessContainment.TerminateCurrentOwnership(
                attachment.ContainmentId,
                attachment.MembershipId);
            await targetExit.ConfigureAwait(false);
            return 205;
        }

        await targetExit.ConfigureAwait(false);
        var exitedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PlatformProcessContainment.PrepareCurrentProcessForMembershipObservation(
            attachment.OwnerLifetimeId);
        var targetExited = new byte[sizeof(byte) + sizeof(int) + sizeof(long)];
        targetExited[0] = TargetExited;
        BinaryPrimitives.WriteInt32LittleEndian(
            targetExited.AsSpan(sizeof(byte)),
            target.ExitCode);
        BinaryPrimitives.WriteInt64LittleEndian(
            targetExited.AsSpan(sizeof(byte) + sizeof(int)),
            exitedAtUnixMilliseconds);
        await WriteStatusAsync(status, targetExited).ConfigureAwait(false);
        if (mutation.HasFlag(ProcessOwnershipMutation.DelayAfterTargetExitReport))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (await ownerLifetime.ConfigureAwait(false) != FinalizeSupervisor)
        {
            PlatformProcessContainment.TerminateCurrentOwnership(
                attachment.ContainmentId,
                attachment.MembershipId);
            return 206;
        }

        await Task.WhenAll(targetStandardOutput, targetStandardError).ConfigureAwait(false);

        return target.ExitCode;
    }

    private static ProcessStartInfo CreateTargetStartInfo(
        LaunchSpecPayload payload,
        OwnershipAttachmentPayload attachment,
        ProcessOwnershipMutation mutation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = payload.FileName,
            WorkingDirectory = payload.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = payload.CloseStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in payload.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var entry in payload.Environment)
        {
            if (entry.Value == null)
            {
                startInfo.Environment.Remove(entry.Key);
            }
            else
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        startInfo.Environment["DOWNKYI_PROCESS_CONTAINMENT_KIND"] =
            OperatingSystem.IsWindows() ? "WindowsJobObject" : "PosixProcessGroup";
        startInfo.Environment["DOWNKYI_PROCESS_CONTAINMENT_ID"] = attachment.ContainmentId;
        startInfo.Environment["DOWNKYI_PROCESS_MEMBERSHIP_ID"] = attachment.MembershipId;
        startInfo.Environment["DOWNKYI_PROCESS_SUPERVISION_MUTATION"] =
            ((int)mutation).ToString(CultureInfo.InvariantCulture);
        return startInfo;
    }

    private static int RunOwnershipProbe()
    {
        var kind = Environment.GetEnvironmentVariable("DOWNKYI_PROCESS_CONTAINMENT_KIND")
            ?? string.Empty;
        var containmentId = Environment.GetEnvironmentVariable(
            "DOWNKYI_PROCESS_CONTAINMENT_ID") ?? string.Empty;
        var membershipId = Environment.GetEnvironmentVariable(
            "DOWNKYI_PROCESS_MEMBERSHIP_ID") ?? string.Empty;
        var owned = PlatformProcessContainment.IsCurrentTargetOwned(
            containmentId,
            membershipId);
        Console.WriteLine(JsonSerializer.Serialize(new OwnershipProbeResult(
            kind,
            containmentId,
            membershipId,
            owned)));
        return owned ? 0 : 42;
    }

    private static int RunLaunchSpecProbe(string argument)
    {
        Console.WriteLine(JsonSerializer.Serialize(new LaunchSpecProbeResult(
            argument,
            Environment.GetEnvironmentVariable("DOWNKYI_LAUNCH_SPEC_PROBE"))));
        return 0;
    }

    private static async Task<int> RunOwnedTreeProbeAsync(string readyPath)
    {
        var assemblyPath = typeof(SupervisorHost).Assembly.Location;
        var childStartInfo = CreateProbeStartInfo(assemblyPath, BlockForeverArgument);
        using var child = Process.Start(childStartInfo)
            ?? throw new InvalidOperationException("The owned descendant probe did not start.");

        await PublishProbeAsync(
                readyPath,
                new OwnedTreeProbeResult(Environment.ProcessId, child.Id),
                IsMutationActive(ProcessOwnershipMutation.FailFixturePublication))
            .ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None)
            .ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunExitWithOwnedDescendantProbeAsync(string readyPath)
    {
        var assemblyPath = typeof(SupervisorHost).Assembly.Location;
        var childStartInfo = CreateProbeStartInfo(assemblyPath, BlockForeverArgument);
        using var child = Process.Start(childStartInfo)
            ?? throw new InvalidOperationException("The inherited-stream descendant did not start.");
        await PublishProbeAsync(
                readyPath,
                new OwnedTreeProbeResult(Environment.ProcessId, child.Id),
                IsMutationActive(ProcessOwnershipMutation.FailFixturePublication))
            .ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunEvidenceHoldProbeAsync(bool withOwnedDescendant)
    {
        var capturePipeTransport = Environment.GetEnvironmentVariable(
            EvidenceHoldEnvironmentVariable);
        Environment.SetEnvironmentVariable(EvidenceHoldEnvironmentVariable, null);
        if (!TryParseEvidenceHoldTransport(
                capturePipeTransport,
                out var completionPipeHandle,
                out var acknowledgmentPipeHandle))
        {
            return 207;
        }

        Process? descendant = null;
        try
        {
            if (withOwnedDescendant)
            {
                var assemblyPath = typeof(SupervisorHost).Assembly.Location;
                descendant = Process.Start(
                    CreateProbeStartInfo(assemblyPath, BlockForeverArgument))
                    ?? throw new InvalidOperationException(
                        "The evidence-hold descendant did not start.");
            }

            using var capturePipe = new AnonymousPipeClientStream(
                PipeDirection.In,
                completionPipeHandle);
            using var acknowledgmentPipe = new AnonymousPipeClientStream(
                PipeDirection.Out,
                acknowledgmentPipeHandle);
            var completion = new byte[1];
            var read = await capturePipe.ReadAsync(completion, CancellationToken.None)
                .ConfigureAwait(false);
            if (read != completion.Length || completion[0] != EvidenceCaptureCompleted)
            {
                return 208;
            }

            await acknowledgmentPipe.WriteAsync(
                    new[] { EvidenceCaptureAcknowledged },
                    CancellationToken.None)
                .ConfigureAwait(false);
            await acknowledgmentPipe.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            descendant?.Dispose();
        }
    }

    private static bool TryParseEvidenceHoldTransport(
        string? transport,
        out string completionPipeHandle,
        out string acknowledgmentPipeHandle)
    {
        completionPipeHandle = string.Empty;
        acknowledgmentPipeHandle = string.Empty;
        if (string.IsNullOrWhiteSpace(transport))
        {
            return false;
        }

        var handles = transport.Split('|', StringSplitOptions.None);
        if (handles.Length != 2 ||
            string.IsNullOrWhiteSpace(handles[0]) ||
            string.IsNullOrWhiteSpace(handles[1]))
        {
            return false;
        }

        completionPipeHandle = handles[0];
        acknowledgmentPipeHandle = handles[1];
        return true;
    }

    private static ProcessStartInfo CreateProbeStartInfo(
        string assemblyPath,
        string argument)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task<int> RunCollectorOutputProbeAsync(
        int exitCode,
        bool largeOutput)
    {
        if (!largeOutput)
        {
            await Console.Out.WriteAsync("collector-stdout").ConfigureAwait(false);
            await Console.Error.WriteAsync("collector-stderr").ConfigureAwait(false);
            return exitCode;
        }

        const int chunkCount = 128;
        const int chunkLength = 4096;
        var stdoutChunk = new string('o', chunkLength);
        var stderrChunk = new string('e', chunkLength);
        var stdout = WriteCollectorChunksAsync(Console.Out, stdoutChunk, chunkCount);
        var stderr = WriteCollectorChunksAsync(Console.Error, stderrChunk, chunkCount);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<int> RunCollectorBlockProbeAsync(
        string readyPath,
        bool publishBeforeBlock)
    {
        await Console.Out.WriteAsync("collector-before-block-stdout")
            .ConfigureAwait(false);
        await Console.Error.WriteAsync("collector-before-block-stderr")
            .ConfigureAwait(false);
        await Task.WhenAll(Console.Out.FlushAsync(), Console.Error.FlushAsync())
            .ConfigureAwait(false);

        if (publishBeforeBlock)
        {
            await PublishProbeAsync(
                    readyPath,
                    new CollectorReadyProbeResult(
                        Environment.ProcessId,
                        BlockingTaskEstablished: false),
                    injectFailure: false)
                .ConfigureAwait(false);
        }

        var blockingTask = Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        if (blockingTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "The collector blocking task completed before publication.");
        }

        if (!publishBeforeBlock)
        {
            await PublishProbeAsync(
                    readyPath,
                    new CollectorReadyProbeResult(
                        Environment.ProcessId,
                        BlockingTaskEstablished: true),
                    injectFailure: false)
                .ConfigureAwait(false);
        }

        await blockingTask.ConfigureAwait(false);
        return 0;
    }

    private static async Task WriteCollectorChunksAsync(
        TextWriter writer,
        string chunk,
        int chunkCount)
    {
        for (var index = 0; index < chunkCount; index++)
        {
            await writer.WriteAsync(chunk).ConfigureAwait(false);
        }
    }

    private static async Task PublishProbeAsync<T>(
        string readyPath,
        T result,
        bool injectFailure)
    {
        var temporaryPath = $"{readyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(result);
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(payload, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (injectFailure)
            {
                throw new IOException("Injected fixture publication failure.");
            }

            File.Move(temporaryPath, readyPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool IsMutationActive(ProcessOwnershipMutation mutation)
    {
        return int.TryParse(
                   Environment.GetEnvironmentVariable("DOWNKYI_PROCESS_SUPERVISION_MUTATION"),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var mutationValue) &&
               ((ProcessOwnershipMutation)mutationValue).HasFlag(mutation);
    }

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        byte expectedType,
        int maximumPayloadBytes)
    {
        var header = await ReadExactAsync(
                stream,
                sizeof(byte) + sizeof(int))
            .ConfigureAwait(false);
        if (header[0] != expectedType)
        {
            throw new InvalidOperationException("The owner sent an invalid supervision protocol state.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(sizeof(byte)));
        if (payloadLength is <= 0 || payloadLength > maximumPayloadBytes)
        {
            throw new InvalidOperationException("The owner sent an invalid supervision payload length.");
        }

        return await ReadExactAsync(stream, payloadLength).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        var payload = new byte[length];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(
                    payload.AsMemory(offset, payload.Length - offset),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The owner-lifetime channel closed during authorization.");
            }

            offset += read;
        }

        return payload;
    }

    private static async Task<byte?> ReadOwnerLifetimeSignalAsync(Stream control)
    {
        var signal = new byte[1];
        var read = await control.ReadAsync(signal, CancellationToken.None).ConfigureAwait(false);
        return read == 0 ? null : signal[0];
    }

    private static Task WriteStatusAsync(Stream status, byte value)
    {
        return WriteStatusAsync(status, new[] { value });
    }

    private static async Task WriteStatusAsync(Stream status, byte[] payload)
    {
        await status.WriteAsync(payload, CancellationToken.None).ConfigureAwait(false);
        await status.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private sealed record OwnershipAttachmentPayload(
        string ContainmentId,
        string MembershipId,
        string OwnerLifetimeId);

    private sealed record LaunchSpecPayload(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string?> Environment,
        bool CloseStandardInput,
        EvidenceHoldTransport? EvidenceHold);

    private sealed record EvidenceHoldTransport(
        string TargetEnvironmentVariable,
        string CompletionClientHandle,
        string AcknowledgmentClientHandle);

    private sealed class EvidenceHoldClientHandleScope : IDisposable
    {
        private readonly EvidenceHoldTransport _transport;
        private readonly AnonymousPipeClientStream _completionPipe;
        private readonly AnonymousPipeClientStream _acknowledgmentPipe;
        private bool _released;

        public EvidenceHoldClientHandleScope(EvidenceHoldTransport transport)
        {
            _transport = transport;
            _completionPipe = new AnonymousPipeClientStream(
                PipeDirection.In,
                transport.CompletionClientHandle);
            _acknowledgmentPipe = new AnonymousPipeClientStream(
                PipeDirection.Out,
                transport.AcknowledgmentClientHandle);
        }

        public void ApplyTo(ProcessStartInfo startInfo)
        {
            startInfo.Environment[_transport.TargetEnvironmentVariable] = string.Join(
                '|',
                _transport.CompletionClientHandle,
                _transport.AcknowledgmentClientHandle);
        }

        public void ReleaseSupervisorCopies()
        {
            if (_released)
            {
                return;
            }

            _completionPipe.Dispose();
            _acknowledgmentPipe.Dispose();
            _released = true;
        }

        public void Dispose()
        {
            ReleaseSupervisorCopies();
        }
    }

    private sealed record OwnershipProbeResult(
        string ContainmentKind,
        string ContainmentId,
        string MembershipId,
        bool OwnershipEstablished);

    private sealed record LaunchSpecProbeResult(string Argument, string? EnvironmentValue);

    private sealed record OwnedTreeProbeResult(int RootProcessId, int ChildProcessId);

    private sealed record CollectorReadyProbeResult(
        int ProcessId,
        bool BlockingTaskEstablished);
}
