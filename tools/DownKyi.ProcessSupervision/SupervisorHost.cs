using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;

namespace DownKyi.ProcessSupervision;

internal static class SupervisorHost
{
    internal const string HostArgument = "--owned-process-host";
    internal const string OwnershipProbeArgument = "--ownership-probe";
    internal const string LaunchSpecProbeArgument = "--launch-spec-probe";
    internal const string BlockForeverArgument = "--block-forever";

    private const byte LaunchAuthorization = 0xC1;
    private const byte OwnershipEstablished = 0xA1;
    private const byte OwnershipMutationActive = 0xA2;
    private const int MaximumLaunchPayloadBytes = 1024 * 1024;

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
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], BlockForeverArgument, StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None)
                .ConfigureAwait(false);
            return 0;
        }

        if (arguments.Count != 5 ||
            !string.Equals(arguments[0], HostArgument, StringComparison.Ordinal) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var mutationValue) ||
            !Enum.IsDefined((ProcessOwnershipMutation)mutationValue))
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
        var jobName = arguments[3];
        var mutation = (ProcessOwnershipMutation)mutationValue;
        using var reader = new BinaryReader(control, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadByte() != LaunchAuthorization)
        {
            return 201;
        }

        var payloadLength = reader.ReadInt32();
        if (payloadLength is <= 0 or > MaximumLaunchPayloadBytes)
        {
            return 202;
        }
        var payloadBytes = reader.ReadBytes(payloadLength);
        if (payloadBytes.Length != payloadLength)
        {
            return 203;
        }

        var ownershipEstablished = PlatformProcessContainment.EstablishCurrentProcessOwnership(
            jobName,
            mutation);
        if (!ownershipEstablished && mutation == ProcessOwnershipMutation.None)
        {
            await status.WriteAsync(new byte[] { 0 }, CancellationToken.None)
                .ConfigureAwait(false);
            await status.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 204;
        }

        var payload = JsonSerializer.Deserialize<LaunchSpecPayload>(payloadBytes)
            ?? throw new InvalidOperationException("The immutable launch specification is invalid.");
        var containmentId = OperatingSystem.IsWindows()
            ? jobName
            : PosixNative.GetProcessGroup().ToString(CultureInfo.InvariantCulture);
        var startInfo = CreateTargetStartInfo(payload, containmentId);
        await status.WriteAsync(
                new[] { ownershipEstablished ? OwnershipEstablished : OwnershipMutationActive },
                CancellationToken.None)
            .ConfigureAwait(false);
        await status.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await status.DisposeAsync().ConfigureAwait(false);
        await control.DisposeAsync().ConfigureAwait(false);

        using var target = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The owned target process did not start.");
        if (payload.CloseStandardInput)
        {
            target.StandardInput.Close();
        }

        await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return target.ExitCode;
    }

    private static ProcessStartInfo CreateTargetStartInfo(
        LaunchSpecPayload payload,
        string containmentId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = payload.FileName,
            WorkingDirectory = payload.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = payload.CloseStandardInput
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
        startInfo.Environment["DOWNKYI_PROCESS_CONTAINMENT_ID"] =
            containmentId;
        return startInfo;
    }

    private static int RunOwnershipProbe()
    {
        var kind = Environment.GetEnvironmentVariable("DOWNKYI_PROCESS_CONTAINMENT_KIND")
            ?? string.Empty;
        var containmentId = Environment.GetEnvironmentVariable(
            "DOWNKYI_PROCESS_CONTAINMENT_ID") ?? string.Empty;
        var owned = PlatformProcessContainment.IsCurrentTargetOwned(containmentId);
        Console.WriteLine(JsonSerializer.Serialize(new OwnershipProbeResult(kind, containmentId, owned)));
        return owned ? 0 : 42;
    }

    private static int RunLaunchSpecProbe(string argument)
    {
        Console.WriteLine(JsonSerializer.Serialize(new LaunchSpecProbeResult(
            argument,
            Environment.GetEnvironmentVariable("DOWNKYI_LAUNCH_SPEC_PROBE"))));
        return 0;
    }

    private sealed record LaunchSpecPayload(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string?> Environment,
        bool CloseStandardInput);

    private sealed record OwnershipProbeResult(
        string ContainmentKind,
        string ContainmentId,
        bool OwnershipEstablished);

    private sealed record LaunchSpecProbeResult(string Argument, string? EnvironmentValue);
}
