using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownKyi.RestartHandoff.Fixture;

#pragma warning disable CA1515 // Platform tests intentionally locate this test-only executable through its marker type.
public sealed class FixtureMarker;
#pragma warning restore CA1515

internal static class Program
{
    private const string AuthorizationMagic = "DKYRST4A";
    private const int AuthorizationVersion = 1;
    private const int AuthorizationNonceLength = 32;
    private const int AuthorizationFrameLength = 8 + sizeof(int) + AuthorizationNonceLength +
        sizeof(long) + sizeof(long) + sizeof(byte);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The executable fixture must convert every unexpected failure into typed cross-process evidence.")]
    public static async Task<int> Main(string[] arguments)
    {
        try
        {
            return arguments.FirstOrDefault() switch
            {
                "parent" => await RunParentAsync(arguments).ConfigureAwait(false),
                "helper" => await RunHelperAsync(arguments).ConfigureAwait(false),
                "replacement" => RunReplacement(arguments),
                "instant-exit" => 0,
                "rebound" => await RunReboundAsync().ConfigureAwait(false),
                "owned-successor" => await RunOwnedSuccessorAsync(arguments).ConfigureAwait(false),
                _ => 64
            };
        }
        catch (Exception failure)
        {
            Emit(new RestartEvidence(
                "FixtureFailure",
                "Terminal",
                CurrentPlatform(),
                null,
                Environment.ProcessId,
                0,
                0,
                0,
                0,
                0,
                failure.GetType().Name,
                failure.Message));
            return 70;
        }
    }

    private static async Task<int> RunParentAsync(string[] arguments)
    {
        if (arguments.Length != 4 ||
            !int.TryParse(arguments[2], out var windowMilliseconds) ||
            windowMilliseconds <= 0)
        {
            return 64;
        }

        var scenario = arguments[1];
        var deadline = DeadlineEnvelope.Create(windowMilliseconds);
        var nonce = RandomNumberGenerator.GetBytes(AuthorizationNonceLength);
        var watcherProcessId = Environment.ProcessId;
        if (scenario == "stale-identity")
        {
            using var stale = StartFixture("instant-exit", redirectStandardInput: false);
            watcherProcessId = stale.Id;
            await stale.WaitForExitAsync().ConfigureAwait(false);
        }

        var helperStartInfo = CreateFixtureStartInfo(
            "helper",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            watcherProcessId.ToString(CultureInfo.InvariantCulture),
            deadline.ExpiresAt.ToString(CultureInfo.InvariantCulture),
            deadline.Frequency.ToString(CultureInfo.InvariantCulture),
            deadline.Domain,
            deadline.WindowMilliseconds.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(nonce),
            scenario,
            arguments[3]);
        helperStartInfo.RedirectStandardInput = true;
        using var helper = Process.Start(helperStartInfo)
            ?? throw new InvalidOperationException("The restart helper fixture did not start.");

        Emit(new RestartEvidence(
            "ParentStarted",
            "Prepared",
            CurrentPlatform(),
            null,
            Environment.ProcessId,
            helper.Id,
            deadline.ExpiresAt,
            DeadlineEnvelope.Now,
            deadline.RemainingTicks,
            0,
            scenario,
            null));

        if (scenario == "late-watcher")
        {
            return 0;
        }

        if (scenario is "stale-identity" or "helper-crash")
        {
            await helper.WaitForExitAsync().ConfigureAwait(false);
            Emit(new RestartEvidence(
                scenario == "helper-crash" ? "HelperCrashObserved" : "HelperExited",
                "Terminal",
                CurrentPlatform(),
                null,
                Environment.ProcessId,
                helper.Id,
                deadline.ExpiresAt,
                DeadlineEnvelope.Now,
                deadline.RemainingTicks,
                0,
                helper.ExitCode.ToString(CultureInfo.InvariantCulture),
                null));
            return 0;
        }

        var command = await Console.In.ReadLineAsync().ConfigureAwait(false);
        switch (command)
        {
            case "EOF":
                helper.StandardInput.Close();
                await helper.WaitForExitAsync().ConfigureAwait(false);
                return 0;
            case "PARTIAL":
                {
                    var frame = CreateAuthorizationFrame(deadline, nonce);
                    await helper.StandardInput.BaseStream.WriteAsync(frame.AsMemory(0, frame.Length / 2))
                        .ConfigureAwait(false);
                    helper.StandardInput.Close();
                    await helper.WaitForExitAsync().ConfigureAwait(false);
                    return 0;
                }
            case "REPLAY":
                {
                    var frame = CreateAuthorizationFrame(deadline, nonce);
                    await helper.StandardInput.BaseStream.WriteAsync(frame).ConfigureAwait(false);
                    await helper.StandardInput.BaseStream.WriteAsync(frame).ConfigureAwait(false);
                    helper.StandardInput.Close();
                    await helper.WaitForExitAsync().ConfigureAwait(false);
                    return 0;
                }
            case "EXIT_PRECOMMIT":
                return 0;
            case "EXHAUST":
                SpinUntil(deadline.ExpiresAt);
                await WriteValidAuthorizationAsync(helper, deadline, nonce).ConfigureAwait(false);
                await helper.WaitForExitAsync().ConfigureAwait(false);
                return 0;
            case "VALID":
                await WriteValidAuthorizationAsync(helper, deadline, nonce).ConfigureAwait(false);
                return 0;
            case "VALID_HOLD":
                await WriteValidAuthorizationAsync(helper, deadline, nonce).ConfigureAwait(false);
                break;
            default:
                if (command?.StartsWith("CONSUME:", StringComparison.Ordinal) == true &&
                    int.TryParse(command.AsSpan("CONSUME:".Length), out var consumeMilliseconds) &&
                    consumeMilliseconds > 0)
                {
                    SpinUntil(checked(DeadlineEnvelope.Now +
                        DeadlineEnvelope.MillisecondsToTicks(consumeMilliseconds)));
                    await WriteValidAuthorizationAsync(helper, deadline, nonce).ConfigureAwait(false);
                    break;
                }

                return 65;
        }

        var terminalCommand = await Console.In.ReadLineAsync().ConfigureAwait(false);
        return terminalCommand == "EXIT" ? 0 : 66;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Watcher establishment must fail closed and retain the exact native failure as typed evidence.")]
    private static async Task<int> RunHelperAsync(string[] arguments)
    {
        if (arguments.Length != 10 ||
            !int.TryParse(arguments[1], out var exactParentProcessId) ||
            !int.TryParse(arguments[2], out var watcherProcessId) ||
            !long.TryParse(arguments[3], out var expiresAt) ||
            !long.TryParse(arguments[4], out var frequency) ||
            !int.TryParse(arguments[6], out var windowMilliseconds))
        {
            return 64;
        }

        var deadline = new DeadlineEnvelope(arguments[5], expiresAt, frequency, windowMilliseconds);
        deadline.ValidateCurrentClock();
        var nonce = Convert.FromHexString(arguments[7]);
        var scenario = arguments[8];
        if (nonce.Length != AuthorizationNonceLength)
        {
            return 64;
        }

        if (scenario == "late-watcher")
        {
            _ = await ReadAuthorizationAsync(deadline, nonce).ConfigureAwait(false);
            await WaitForLateWatcherReleaseAsync(arguments[9]).ConfigureAwait(false);
        }

        Process? rebound = null;
        ParentExitWatcher? watcher = null;
        try
        {
            if (scenario == "numeric-authority")
            {
                rebound = StartFixture("rebound", redirectStandardInput: true);
                watcherProcessId = rebound.Id;
            }

            try
            {
                watcher = ParentExitWatcher.Create(watcherProcessId);
            }
            catch (Exception failure)
            {
                Emit(new RestartEvidence(
                    "WatcherRejected",
                    "Terminal",
                    CurrentPlatform(),
                    null,
                    exactParentProcessId,
                    Environment.ProcessId,
                    deadline.ExpiresAt,
                    DeadlineEnvelope.Now,
                    deadline.RemainingTicks,
                    0,
                    failure.GetType().Name,
                    scenario));
                return 0;
            }

            Emit(new RestartEvidence(
                "WatcherReady",
                "Ready",
                CurrentPlatform(),
                scenario == "numeric-authority" ? "NumericPidMutation" : watcher.Authority,
                exactParentProcessId,
                Environment.ProcessId,
                deadline.ExpiresAt,
                DeadlineEnvelope.Now,
                deadline.RemainingTicks,
                0,
                scenario,
                null));

            if (scenario == "helper-crash")
            {
                return 73;
            }

            var authorization = await ReadAuthorizationAsync(deadline, nonce).ConfigureAwait(false);
            if (!authorization.Accepted)
            {
                Emit(new RestartEvidence(
                    "AuthorizationRejected",
                    "Terminal",
                    CurrentPlatform(),
                    watcher.Authority,
                    exactParentProcessId,
                    Environment.ProcessId,
                    deadline.ExpiresAt,
                    DeadlineEnvelope.Now,
                    deadline.RemainingTicks,
                    0,
                    authorization.Reason,
                    scenario));
                return 0;
            }

            Emit(new RestartEvidence(
                "Authorized",
                "Committed",
                CurrentPlatform(),
                watcher.Authority,
                exactParentProcessId,
                Environment.ProcessId,
                deadline.ExpiresAt,
                DeadlineEnvelope.Now,
                deadline.RemainingTicks,
                0,
                scenario,
                null));

            if (scenario == "fresh-clock")
            {
                var renewedDeadline = checked(DeadlineEnvelope.Now +
                    DeadlineEnvelope.MillisecondsToTicks(deadline.WindowMilliseconds));
                Emit(new RestartEvidence(
                    "DeadlineMutationRejected",
                    "Terminal",
                    CurrentPlatform(),
                    watcher.Authority,
                    exactParentProcessId,
                    Environment.ProcessId,
                    deadline.ExpiresAt,
                    renewedDeadline,
                    renewedDeadline - deadline.ExpiresAt,
                    0,
                    "FreshClockWouldExtendAuthenticatedDeadline",
                    scenario));
                return 0;
            }

            if (scenario == "numeric-authority")
            {
                rebound!.StandardInput.Close();
                var mutationWait = watcher.WaitUntil(deadline);
                Emit(new RestartEvidence(
                    "NumericAuthorityMutationRejected",
                    "Terminal",
                    CurrentPlatform(),
                    "NumericPidMutation",
                    exactParentProcessId,
                    Environment.ProcessId,
                    deadline.ExpiresAt,
                    DeadlineEnvelope.Now,
                    deadline.RemainingTicks,
                    0,
                    mutationWait.Exited ? "FalseParentExitWhileExactParentAlive" : mutationWait.Reason,
                    scenario));
                return 0;
            }

            if (scenario == "pid-reuse")
            {
                using var numericCandidate = StartFixture("rebound", redirectStandardInput: true);
                var numericCandidateProcessId = numericCandidate.Id;
                numericCandidate.StandardInput.Close();
                await numericCandidate.WaitForExitAsync().ConfigureAwait(false);
                if (watcher.IsExited())
                {
                    throw new InvalidOperationException(
                        "The exact watcher followed a reused numeric identity candidate.");
                }

                Emit(new RestartEvidence(
                    "NumericReuseIgnored",
                    "Committed",
                    CurrentPlatform(),
                    watcher.Authority,
                    exactParentProcessId,
                    Environment.ProcessId,
                    deadline.ExpiresAt,
                    DeadlineEnvelope.Now,
                    deadline.RemainingTicks,
                    0,
                    numericCandidateProcessId.ToString(CultureInfo.InvariantCulture),
                    scenario));
            }

            var wait = watcher.WaitUntil(deadline);
            if (!wait.Exited)
            {
                Emit(new RestartEvidence(
                    "DeadlineExceeded",
                    "Terminal",
                    CurrentPlatform(),
                    watcher.Authority,
                    exactParentProcessId,
                    Environment.ProcessId,
                    deadline.ExpiresAt,
                    DeadlineEnvelope.Now,
                    deadline.RemainingTicks,
                    0,
                    wait.Reason,
                    scenario));
                return 0;
            }

            Emit(new RestartEvidence(
                "ParentExitObserved",
                "ExactParentExited",
                CurrentPlatform(),
                watcher.Authority,
                exactParentProcessId,
                Environment.ProcessId,
                deadline.ExpiresAt,
                DeadlineEnvelope.Now,
                deadline.RemainingTicks,
                0,
                scenario,
                null));

            const int relaunchAttempts = 1;
            Emit(new RestartEvidence(
                "RelaunchAttempted",
                "Relaunching",
                CurrentPlatform(),
                watcher.Authority,
                exactParentProcessId,
                Environment.ProcessId,
                deadline.ExpiresAt,
                DeadlineEnvelope.Now,
                deadline.RemainingTicks,
                relaunchAttempts,
                scenario,
                null));

            if (scenario == "relaunch-failure")
            {
                try
                {
                    _ = Process.Start(new ProcessStartInfo
                    {
                        FileName = Path.Combine(Path.GetTempPath(),
                            $"downkyi-missing-restart-{Guid.NewGuid():N}"),
                        UseShellExecute = false
                    });
                }
                catch (Win32Exception failure)
                {
                    Emit(new RestartEvidence(
                        "RelaunchFailed",
                        "Terminal",
                        CurrentPlatform(),
                        watcher.Authority,
                        exactParentProcessId,
                        Environment.ProcessId,
                        deadline.ExpiresAt,
                        DeadlineEnvelope.Now,
                        deadline.RemainingTicks,
                        relaunchAttempts,
                        failure.GetType().Name,
                        scenario));
                    return 0;
                }

                throw new InvalidOperationException("The missing replacement unexpectedly started.");
            }

            using var replacement = StartFixture(
                "replacement",
                redirectStandardInput: false,
                Convert.ToHexString(nonce));
            await replacement.WaitForExitAsync().ConfigureAwait(false);
            if (replacement.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The replacement fixture exited with {replacement.ExitCode}.");
            }

            Emit(new RestartEvidence(
                "HelperTerminal",
                "Terminal",
                CurrentPlatform(),
                watcher.Authority,
                exactParentProcessId,
                Environment.ProcessId,
                deadline.ExpiresAt,
                DeadlineEnvelope.Now,
                deadline.RemainingTicks,
                relaunchAttempts,
                "Completed",
                scenario));
            return 0;
        }
        finally
        {
            watcher?.Dispose();
            if (rebound is { HasExited: false })
            {
                rebound.StandardInput.Close();
                await rebound.WaitForExitAsync().ConfigureAwait(false);
            }

            rebound?.Dispose();
        }
    }

    private static async Task WaitForLateWatcherReleaseAsync(string pipeName)
    {
        using var gate = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.In,
            PipeOptions.Asynchronous);
        await gate.ConnectAsync().ConfigureAwait(false);
        var release = new byte[1];
        if (await gate.ReadAsync(release).ConfigureAwait(false) != 1 || release[0] != 1)
        {
            throw new EndOfStreamException(
                "The late-watcher synchronization gate closed before parent-exit release.");
        }
    }

    private static int RunReplacement(string[] arguments)
    {
        Emit(new RestartEvidence(
            "ReplacementStarted",
            "Running",
            CurrentPlatform(),
            null,
            0,
            Environment.ProcessId,
            0,
            DeadlineEnvelope.Now,
            0,
            1,
            arguments.Length > 1 ? arguments[1] : null,
            null));
        return 0;
    }

    private static async Task<int> RunReboundAsync()
    {
        _ = await Console.In.ReadLineAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunOwnedSuccessorAsync(string[] arguments)
    {
        if (arguments.Length != 2)
        {
            return 64;
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            arguments[1],
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync().ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(
                new OwnedSuccessorEvidence("Ready", false, 0), JsonOptions))
            .ConfigureAwait(false);
        var authorization = await reader.ReadLineAsync().ConfigureAwait(false);
        if (!string.Equals(authorization, "COMMIT", StringComparison.Ordinal))
        {
            return 65;
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(
                new OwnedSuccessorEvidence("Committed", true, 0), JsonOptions))
            .ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static async Task<AuthorizationResult> ReadAuthorizationAsync(
        DeadlineEnvelope deadline,
        byte[] expectedNonce)
    {
        using var input = Console.OpenStandardInput();
        using var payload = new MemoryStream();
        var buffer = new byte[AuthorizationFrameLength];
        while (true)
        {
            var read = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await payload.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            if (payload.Length > AuthorizationFrameLength * 2L)
            {
                return new AuthorizationResult(false, "AuthorizationOverflow");
            }
        }

        var bytes = payload.ToArray();
        if (bytes.Length == 0)
        {
            return new AuthorizationResult(false, "AuthorizationEof");
        }

        if (bytes.Length < AuthorizationFrameLength)
        {
            return new AuthorizationResult(false, "PartialAuthorization");
        }

        if (bytes.Length == AuthorizationFrameLength * 2 &&
            bytes.AsSpan(0, AuthorizationFrameLength)
                .SequenceEqual(bytes.AsSpan(AuthorizationFrameLength)))
        {
            return new AuthorizationResult(false, "ReplayedAuthorization");
        }

        if (bytes.Length != AuthorizationFrameLength)
        {
            return new AuthorizationResult(false, "AuthorizationLengthMismatch");
        }

        var span = bytes.AsSpan();
        if (!span[..8].SequenceEqual(Encoding.ASCII.GetBytes(AuthorizationMagic)) ||
            BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, sizeof(int))) !=
                AuthorizationVersion ||
            !span.Slice(12, AuthorizationNonceLength).SequenceEqual(expectedNonce) ||
            BinaryPrimitives.ReadInt64LittleEndian(
                span.Slice(12 + AuthorizationNonceLength, sizeof(long))) !=
                deadline.ExpiresAt ||
            BinaryPrimitives.ReadInt64LittleEndian(
                span.Slice(12 + AuthorizationNonceLength + sizeof(long), sizeof(long))) !=
                deadline.Frequency ||
            span[^1] != 1)
        {
            return new AuthorizationResult(false, "AuthorizationMismatch");
        }

        return deadline.RemainingTicks > 0
            ? new AuthorizationResult(true, "Committed")
            : new AuthorizationResult(false, "DeadlineExhaustedBeforeCommit");
    }

    private static byte[] CreateAuthorizationFrame(DeadlineEnvelope deadline, byte[] nonce)
    {
        var frame = new byte[AuthorizationFrameLength];
        Encoding.ASCII.GetBytes(AuthorizationMagic).CopyTo(frame, 0);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), AuthorizationVersion);
        nonce.CopyTo(frame, 12);
        BinaryPrimitives.WriteInt64LittleEndian(
            frame.AsSpan(12 + AuthorizationNonceLength), deadline.ExpiresAt);
        BinaryPrimitives.WriteInt64LittleEndian(
            frame.AsSpan(12 + AuthorizationNonceLength + sizeof(long)), deadline.Frequency);
        frame[^1] = 1;
        return frame;
    }

    private static async Task WriteValidAuthorizationAsync(
        Process helper,
        DeadlineEnvelope deadline,
        byte[] nonce)
    {
        await helper.StandardInput.BaseStream.WriteAsync(CreateAuthorizationFrame(deadline, nonce))
            .ConfigureAwait(false);
        helper.StandardInput.Close();
    }

    private static Process StartFixture(
        string mode,
        bool redirectStandardInput,
        params string[] arguments)
    {
        var startInfo = CreateFixtureStartInfo(new[] { mode }.Concat(arguments).ToArray());
        startInfo.RedirectStandardInput = redirectStandardInput;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The '{mode}' fixture did not start.");
    }

    private static ProcessStartInfo CreateFixtureStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(typeof(FixtureMarker).Assembly.Location);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void SpinUntil(long targetTimestamp)
    {
        var spinner = new SpinWait();
        while (DeadlineEnvelope.Now < targetTimestamp)
        {
            spinner.SpinOnce();
        }
    }

    private static void Emit(RestartEvidence evidence)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(evidence, JsonOptions));
        Console.Out.Flush();
    }

    private static string CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        return "Unsupported";
    }

    private sealed record AuthorizationResult(bool Accepted, string Reason);

    private sealed record OwnedSuccessorEvidence(string State, bool Committed, int RelaunchAttempts);
}

internal sealed record RestartEvidence(
    string Type,
    string State,
    string Platform,
    string? Authority,
    int ParentProcessId,
    int HelperProcessId,
    long PreparedDeadline,
    long ObservedTimestamp,
    long RemainingTicks,
    int RelaunchAttempts,
    string? Outcome,
    string? Mutation);

internal sealed record DeadlineEnvelope(
    string Domain,
    long ExpiresAt,
    long Frequency,
    int WindowMilliseconds)
{
    public static long Now => Stopwatch.GetTimestamp();

    public long RemainingTicks => Math.Max(0, ExpiresAt - Now);

    public static DeadlineEnvelope Create(int windowMilliseconds)
    {
        var domain = OperatingSystem.IsWindows()
            ? "windows-qpc-v1"
            : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? "unix-monotonic-nanoseconds-v1"
                : throw new PlatformNotSupportedException();
        return new DeadlineEnvelope(
            domain,
            checked(Now + MillisecondsToTicks(windowMilliseconds)),
            Stopwatch.Frequency,
            windowMilliseconds);
    }

    public void ValidateCurrentClock()
    {
        var expectedDomain = OperatingSystem.IsWindows()
            ? "windows-qpc-v1"
            : "unix-monotonic-nanoseconds-v1";
        if (!string.Equals(Domain, expectedDomain, StringComparison.Ordinal) ||
            Frequency != Stopwatch.Frequency)
        {
            throw new InvalidOperationException("The handoff monotonic clock domain does not match.");
        }
    }

    public static long MillisecondsToTicks(int milliseconds)
    {
        return checked((long)Math.Ceiling(milliseconds * (double)Stopwatch.Frequency / 1000d));
    }

    public int RemainingMillisecondsCeiling()
    {
        var remaining = RemainingTicks;
        return remaining <= 0
            ? 0
            : checked((int)Math.Min(
                int.MaxValue,
                Math.Ceiling(remaining * 1000d / Frequency)));
    }
}

internal readonly record struct ParentWaitResult(bool Exited, string Reason);

internal abstract class ParentExitWatcher : IDisposable
{
    public abstract string Authority { get; }

    public abstract bool IsExited();

    public abstract ParentWaitResult WaitUntil(DeadlineEnvelope deadline);

    public abstract void Dispose();

    public static ParentExitWatcher Create(int processId)
    {
        ParentExitWatcher watcher = OperatingSystem.IsWindows()
            ? new WindowsParentExitWatcher(processId)
            : OperatingSystem.IsLinux()
                ? new LinuxParentExitWatcher(processId)
                : OperatingSystem.IsMacOS()
                    ? new MacOsParentExitWatcher(processId)
                    : throw new PlatformNotSupportedException();
        if (watcher.IsExited())
        {
            watcher.Dispose();
            throw new InvalidOperationException(
                "The exact parent watcher was already signaled before READY.");
        }

        return watcher;
    }
}

[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare finalizer",
    Justification = "The fixture owns a bounded native process handle and every executable path disposes it deterministically.")]
internal sealed class WindowsParentExitWatcher : ParentExitWatcher
{
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private readonly nint _handle;

    public WindowsParentExitWatcher(int processId)
    {
        _handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, processId);
        if (_handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "OpenProcess could not acquire the exact parent process object.");
        }

        if (!GetExitCodeProcess(_handle, out var exitCode) || exitCode != StillActive)
        {
            Dispose();
            throw new InvalidOperationException("The Windows parent process was not live at arm time.");
        }
    }

    public override string Authority => "WindowsProcessHandle";

    public override bool IsExited()
    {
        return WaitForSingleObject(_handle, 0) switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            var result => throw new Win32Exception(
                Marshal.GetLastPInvokeError(), $"WaitForSingleObject returned {result}.")
        };
    }

    public override ParentWaitResult WaitUntil(DeadlineEnvelope deadline)
    {
        var result = WaitForSingleObject(_handle, (uint)deadline.RemainingMillisecondsCeiling());
        return result switch
        {
            WaitObject0 => new ParentWaitResult(true, "ExactProcessObjectSignaled"),
            WaitTimeout => new ParentWaitResult(false, "DeadlineExceeded"),
            _ => throw new Win32Exception(
                Marshal.GetLastPInvokeError(), $"WaitForSingleObject returned {result}.")
        };
    }

    public override void Dispose()
    {
        if (_handle != 0)
        {
            _ = CloseHandle(_handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

internal sealed class LinuxParentExitWatcher : ParentExitWatcher
{
    private const nint PidfdOpenSystemCall = 434;
    private const short PollIn = 0x0001;
    private const short PollError = 0x0008;
    private const short PollHangup = 0x0010;
    private const short PollInvalid = 0x0020;
    private readonly int _pidfd;

    public LinuxParentExitWatcher(int processId)
    {
        if (RuntimeInformation.ProcessArchitecture is not Architecture.X64 and
            not Architecture.Arm64)
        {
            throw new PlatformNotSupportedException(
                $"pidfd_open syscall mapping is unavailable for {RuntimeInformation.ProcessArchitecture}.");
        }

        var result = syscall(PidfdOpenSystemCall, processId, 0);
        if (result == -1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "pidfd_open could not acquire the exact parent task.");
        }

        _pidfd = checked((int)result);
    }

    public override string Authority => "LinuxPidFd";

    public override bool IsExited()
    {
        var descriptor = new PollDescriptor { FileDescriptor = _pidfd, Events = PollIn };
        var result = poll(ref descriptor, 1, 0);
        if (result < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "poll failed for pidfd.");
        }

        return result > 0 && IsExitEvent(descriptor.ReturnedEvents);
    }

    public override ParentWaitResult WaitUntil(DeadlineEnvelope deadline)
    {
        var descriptor = new PollDescriptor { FileDescriptor = _pidfd, Events = PollIn };
        var result = poll(ref descriptor, 1, deadline.RemainingMillisecondsCeiling());
        if (result < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "poll failed for pidfd.");
        }

        return result == 0
            ? new ParentWaitResult(false, "DeadlineExceeded")
            : IsExitEvent(descriptor.ReturnedEvents)
                ? new ParentWaitResult(true, "PidFdReadable")
                : throw new InvalidOperationException(
                    $"pidfd poll returned unexpected events {descriptor.ReturnedEvents}.");
    }

    public override void Dispose()
    {
        if (_pidfd >= 0)
        {
            _ = close(_pidfd);
        }
    }

    private static bool IsExitEvent(short events)
    {
        if ((events & (PollError | PollInvalid)) != 0)
        {
            throw new InvalidOperationException($"pidfd reported invalid events {events}.");
        }

        return (events & (PollIn | PollHangup)) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint syscall(nint number, int processId, uint flags);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int poll(ref PollDescriptor descriptors, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int close(int fileDescriptor);
}

internal sealed class MacOsParentExitWatcher : ParentExitWatcher
{
    private const short EventFilterProcess = -5;
    private const ushort EventAdd = 0x0001;
    private const ushort EventEnable = 0x0004;
    private const ushort EventOneShot = 0x0010;
    private const ushort EventReceipt = 0x0040;
    private const ushort EventError = 0x4000;
    private const uint NoteExit = 0x80000000;
    private readonly int _queue;

    public MacOsParentExitWatcher(int processId)
    {
        _queue = kqueue();
        if (_queue < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "kqueue creation failed.");
        }

        var change = new[]
        {
            new KernelEvent
            {
                Identifier = (nuint)processId,
                Filter = EventFilterProcess,
                Flags = EventAdd | EventEnable | EventOneShot | EventReceipt,
                FilterFlags = NoteExit
            }
        };
        var receipt = new KernelEvent[1];
        var zero = new NativeTimespec();
        var result = kevent(_queue, change, 1, receipt, 1, ref zero);
        if (result != 1 ||
            (receipt[0].Flags & EventError) == 0 ||
            receipt[0].Data != 0)
        {
            var error = receipt[0].Data == 0
                ? Marshal.GetLastPInvokeError()
                : checked((int)receipt[0].Data);
            Dispose();
            throw new Win32Exception(error,
                "EVFILT_PROC NOTE_EXIT could not be armed before READY.");
        }
    }

    public override string Authority => "MacOsKqueueProcessNote";

    public override bool IsExited()
    {
        var events = new KernelEvent[1];
        var zero = new NativeTimespec();
        var result = kevent(_queue, null, 0, events, 1, ref zero);
        if (result < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "kevent readiness query failed.");
        }

        return result == 1 && IsExitEvent(events[0]);
    }

    public override ParentWaitResult WaitUntil(DeadlineEnvelope deadline)
    {
        var events = new KernelEvent[1];
        var remaining = deadline.RemainingTicks;
        var timeout = new NativeTimespec
        {
            Seconds = remaining / deadline.Frequency,
            Nanoseconds = checked((nint)((remaining % deadline.Frequency) *
                1_000_000_000L / deadline.Frequency))
        };
        var result = kevent(_queue, null, 0, events, 1, ref timeout);
        if (result < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "kevent exact-parent wait failed.");
        }

        return result == 0
            ? new ParentWaitResult(false, "DeadlineExceeded")
            : IsExitEvent(events[0])
                ? new ParentWaitResult(true, "KqueueNoteExit")
                : throw new InvalidOperationException("kqueue returned a non-exit process event.");
    }

    public override void Dispose()
    {
        if (_queue >= 0)
        {
            _ = close(_queue);
        }
    }

    private static bool IsExitEvent(KernelEvent processEvent)
    {
        if ((processEvent.Flags & EventError) != 0)
        {
            throw new Win32Exception(checked((int)processEvent.Data),
                "kqueue process watcher reported EV_ERROR.");
        }

        return processEvent.Filter == EventFilterProcess &&
            (processEvent.FilterFlags & NoteExit) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KernelEvent
    {
        public nuint Identifier;
        public short Filter;
        public ushort Flags;
        public uint FilterFlags;
        public nint Data;
        public nint UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTimespec
    {
        public long Seconds;
        public nint Nanoseconds;
    }

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int kqueue();

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int kevent(
        int queue,
        [In] KernelEvent[]? changes,
        int changeCount,
        [Out] KernelEvent[]? events,
        int eventCount,
        ref NativeTimespec timeout);

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int close(int fileDescriptor);
}
