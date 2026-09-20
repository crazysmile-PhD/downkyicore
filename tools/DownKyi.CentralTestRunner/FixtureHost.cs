using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.CentralTestRunner;

internal static class FixtureHost
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        return args[0] switch
        {
            "fixture-hold" => await RunHoldAsync().ConfigureAwait(false),
            "fixture-directory-lock" => await RunDirectoryLockAsync(args).ConfigureAwait(false),
            "fixture-pass" => RunPass(),
            "fixture-hold-marker" => await RunHoldMarkerAsync(args).ConfigureAwait(false),
            "fixture-exit-with-pipe-holder" => await RunExitWithPipeHolderAsync(args).ConfigureAwait(false),
            "fixture-stderr-hold" => await RunStderrHoldAsync().ConfigureAwait(false),
            "fixture-tree-root" => await RunTreeRootAsync(args).ConfigureAwait(false),
            "fixture-tree-child" => await RunTreeChildAsync(args).ConfigureAwait(false),
            "fixture-sensitive-hold" => await RunSensitiveHoldAsync(args).ConfigureAwait(false),
            "fixture-long-line" => await RunLongLineAsync(args).ConfigureAwait(false),
            "fixture-dual-output" => await RunDualOutputAsync().ConfigureAwait(false),
            "fixture-windows-process-snapshot" when OperatingSystem.IsWindows() =>
                RunWindowsProcessSnapshot(),
            _ => null
        };
    }

    private static async Task<int> RunHoldAsync()
    {
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        var startTimeUtc = new DateTimeOffset(currentProcess.StartTime.ToUniversalTime());
        Console.WriteLine(
            $"fixture-ready pid={Environment.ProcessId} start={startTimeUtc:O}");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int?> RunDirectoryLockAsync(string[] args)
    {
        if (args.Length <= 1)
        {
            return null;
        }

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

    private static int RunPass()
    {
        Console.WriteLine($"fixture-pass pid={Environment.ProcessId}");
        return 0;
    }

    private static async Task<int?> RunHoldMarkerAsync(string[] args)
    {
        if (args.Length <= 1)
        {
            return null;
        }

        await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int?> RunExitWithPipeHolderAsync(string[] args)
    {
        if (args.Length <= 2)
        {
            return null;
        }

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

    private static async Task<int> RunStderrHoldAsync()
    {
        await Console.Error.WriteLineAsync("fixture-stderr-ready").ConfigureAwait(false);
        await Console.Error.FlushAsync().ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int?> RunTreeRootAsync(string[] args)
    {
        if (args.Length <= 2)
        {
            return null;
        }

        await File.WriteAllTextAsync(Path.Combine(args[2], "root.pid"),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        var childInfo = CreateFixtureChild(args[1], "fixture-tree-child", args[1], args[2]);
        using var child = Process.Start(childInfo)
            ?? throw new InvalidOperationException("The tree child did not start.");
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int?> RunTreeChildAsync(string[] args)
    {
        if (args.Length <= 2)
        {
            return null;
        }

        var grandchildInfo = CreateFixtureChild(
            args[1], "fixture-hold-marker", Path.Combine(args[2], "grandchild.pid"));
        using var grandchild = Process.Start(grandchildInfo)
            ?? throw new InvalidOperationException("The tree grandchild did not start.");
        await File.WriteAllTextAsync(Path.Combine(args[2], "child.pid"),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int?> RunSensitiveHoldAsync(string[] args)
    {
        if (args.Length <= 4)
        {
            return null;
        }

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

    private static async Task<int?> RunLongLineAsync(string[] args)
    {
        if (args.Length <= 1)
        {
            return null;
        }

        await Console.Out.WriteAsync($"token={args[1]}{new string('x', 32768)}").ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
        if (args.Length > 2)
        {
            await File.WriteAllTextAsync(args[2], "ready").ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }
        return 1;
    }

    private static int RunWindowsProcessSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 2;
        }

        foreach (var pair in WindowsProcessRelationshipSnapshot.ReadParentIds())
        {
            Console.WriteLine($"{pair.Key}|{pair.Value}");
        }

        return 0;
    }

    private static async Task<int> RunDualOutputAsync()
    {
        var payload = new string('x', 128 * 1024);
        await Console.Out.WriteAsync(payload).ConfigureAwait(false);
        await Console.Error.WriteAsync(payload).ConfigureAwait(false);
        return 0;
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
