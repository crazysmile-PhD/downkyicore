using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.CentralTestRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && string.Equals(args[0], "owned-scope-host", StringComparison.Ordinal))
        {
            return await ProcessLifecycleOwner.RunHostAsync(args[1], args[2]).ConfigureAwait(false);
        }

        if (args.Length > 0 && string.Equals(args[0], "fixture-hold", StringComparison.Ordinal))
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var startTimeUtc = new DateTimeOffset(currentProcess.StartTime.ToUniversalTime());
            Console.WriteLine(
                $"fixture-ready pid={Environment.ProcessId} start={startTimeUtc:O}");
            await Console.Out.FlushAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 1 && string.Equals(args[0], "fixture-directory-lock", StringComparison.Ordinal))
        {
            using var directoryLock = NativeMethods.CreateFile(
                args[1],
                NativeMethods.ListDirectory,
                NativeMethods.ShareRead | NativeMethods.ShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.BackupSemantics,
                IntPtr.Zero);
            if (directoryLock.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var lockReferenceAdded = false;
            directoryLock.DangerousAddRef(ref lockReferenceAdded);
            try
            {
                await Console.Out.WriteLineAsync("fixture-lock-ready").ConfigureAwait(false);
                await Console.Out.FlushAsync().ConfigureAwait(false);
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            }
            finally
            {
                if (lockReferenceAdded)
                {
                    directoryLock.DangerousRelease();
                }
            }

            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "fixture-pass", StringComparison.Ordinal))
        {
            Console.WriteLine($"fixture-pass pid={Environment.ProcessId}");
            await Console.Error.WriteLineAsync("fixture-pass stderr").ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "fixture-stdin-eof", StringComparison.Ordinal))
        {
            return await Console.In.ReadLineAsync().ConfigureAwait(false) is null ? 0 : 3;
        }

        if (args.Length > 1 && string.Equals(args[0], "fixture-gated-stdout", StringComparison.Ordinal))
        {
            using var gate = new NamedPipeClientStream(".", args[1],
                PipeDirection.In, PipeOptions.Asynchronous);
            await gate.ConnectAsync().ConfigureAwait(false);
            var signal = new byte[1];
            await gate.ReadExactlyAsync(signal).ConfigureAwait(false);
            await Console.Out.WriteLineAsync("relay-fault-trigger").ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 1 && string.Equals(args[0], "fixture-hold-marker", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 2 && string.Equals(args[0], "fixture-exit-with-pipe-holder", StringComparison.Ordinal))
        {
            var childInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            childInfo.ArgumentList.Add("exec");
            childInfo.ArgumentList.Add("--runtimeconfig");
            childInfo.ArgumentList.Add(args[1]);
            childInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            childInfo.ArgumentList.Add("fixture-hold");
            using var child = Process.Start(childInfo)
                ?? throw new InvalidOperationException("The pipe-holder fixture did not start.");
            await File.WriteAllTextAsync(args[2], child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "fixture-stderr-hold", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("fixture-stderr-ready").ConfigureAwait(false);
            await Console.Error.FlushAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 2 && string.Equals(args[0], "fixture-tree-root", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(args[2], "root.pid"),
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            var childInfo = CreateFixtureChild(args[1], "fixture-tree-child", args[1], args[2]);
            using var child = Process.Start(childInfo)
                ?? throw new InvalidOperationException("The tree child did not start.");
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 2 && string.Equals(args[0], "fixture-tree-child", StringComparison.Ordinal))
        {
            var grandchildInfo = CreateFixtureChild(
                args[1], "fixture-hold-marker", Path.Combine(args[2], "grandchild.pid"));
            using var grandchild = Process.Start(grandchildInfo)
                ?? throw new InvalidOperationException("The tree grandchild did not start.");
            await File.WriteAllTextAsync(Path.Combine(args[2], "child.pid"),
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 4 && string.Equals(args[0], "fixture-sensitive-hold", StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync($"Authorization: Bearer {args[1]}").ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                $"authenticated=https://example.invalid/video?access_token={args[2]}&mid={args[3]}")
                .ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                $"personal-path={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}")
                .ConfigureAwait(false);
            await Console.Error.WriteLineAsync($"Cookie: SESSDATA={args[4]}; bili_jct={args[1]}")
                .ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
            await Console.Error.FlushAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 1 && string.Equals(args[0], "fixture-long-line", StringComparison.Ordinal))
        {
            await Console.Out.WriteAsync($"token={args[1]}{new string('x', 32768)}").ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
            if (args.Length > 2)
            {
                await File.WriteAllTextAsync(args[2], "ready").ConfigureAwait(false);
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            }
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        return await RunCommandAsync(args, cancellation.Token).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateFixtureChild(string runtimeConfig, string mode, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfig);
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(mode);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static async Task<int> RunCommandAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        return await RunCommandAsync(args, CentralTestCommand.RunAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The command boundary records any primary failure before returning its formal exit code.")]
    internal static async Task<int> RunCommandAsync(
        string[] args,
        Func<string[], CancellationToken, Task<int>> runCommandAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runCommandAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            try
            {
                var repositoryRoot = Path.GetFullPath(
                    FindOption(args, "--repository-root") ?? Directory.GetCurrentDirectory());
                var evidenceDirectory = Path.GetFullPath(
                    FindOption(args, "--evidence-directory") ??
                    Path.Combine(repositoryRoot, "artifacts", "test-flight-recorder"),
                    repositoryRoot);
                await FlightRecorder.PreserveCommandFailureAsync(
                    evidenceDirectory,
                    repositoryRoot,
                    args.FirstOrDefault() ?? "unknown",
                    exception).ConfigureAwait(false);
            }
            catch (Exception evidenceException) when (evidenceException is not OperationCanceledException)
            {
                // A broken evidence destination cannot change the primary command failure.
            }

            return 2;
        }
    }

    private static string? FindOption(string[] args, string option)
    {
        for (var index = args.Length - 2; index >= 0; index--)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static class NativeMethods
    {
        internal const uint ShareRead = 0x00000001;
        internal const uint ShareWrite = 0x00000002;
        internal const uint ListDirectory = 0x00000001;
        internal const uint OpenExisting = 3;
        internal const uint BackupSemantics = 0x02000000;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }
}
