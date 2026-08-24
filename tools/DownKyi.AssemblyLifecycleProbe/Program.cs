using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownKyi.AssemblyLifecycleProbe;

internal static class Program
{
    private const string TestAssemblyLoadOwnerKey = "DownKyi.CentralTestAssemblyLoadOwner";
    private const string TestAssemblyLoadOwnerValue = "DownKyi.AssemblyLifecycleProbe";
    private const string CapturePipeEnvironmentVariable = "DOWNKYI_FORENSICS_CAPTURE_PIPE";
    private const string ChildReleasePipeEnvironmentVariable =
        "DOWNKYI_TRANSIENT_CHILD_RELEASE_PIPE";
    private const string ChildReleaseParentWaitEnvironmentVariable =
        "DOWNKYI_TRANSIENT_CHILD_PARENT_WAIT";
    private const int CaptureCompleted = 0xA5;
    private const int ChildReleaseCompleted = 0xD7;
    private const int ChildReleaseAcknowledged = 0xA7;

    public static int Main(string[] args)
    {
        if (TryReadChildHoldArguments(args, out var childHoldMilliseconds))
        {
            var releasePipeHandle = Environment.GetEnvironmentVariable(
                ChildReleasePipeEnvironmentVariable);
            Environment.SetEnvironmentVariable(ChildReleasePipeEnvironmentVariable, null);
            if (!string.IsNullOrWhiteSpace(releasePipeHandle))
            {
                using var releasePipe = new System.IO.Pipes.NamedPipeClientStream(
                    ".",
                    releasePipeHandle,
                    System.IO.Pipes.PipeDirection.InOut);
                releasePipe.Connect(5_000);
                if (releasePipe.ReadByte() != ChildReleaseCompleted)
                {
                    return 1;
                }

                releasePipe.WriteByte(ChildReleaseAcknowledged);
                releasePipe.Flush();
                return 0;
            }

            Thread.Sleep(childHoldMilliseconds);
            return 0;
        }

        if (TryReadResidualChildArguments(args, out var residualChildHoldMilliseconds))
        {
            return RunResidualChildProbe(residualChildHoldMilliseconds);
        }

        if (!TryReadArguments(args, out var assemblyPath))
        {
            WriteResult(new ProbeResult(false, null, null, false, "invalid_arguments", null));
            return 2;
        }

        var fullPath = Path.GetFullPath(assemblyPath);
        var loaded = LoadAndRequestUnload(fullPath);
        var unloaded = WaitForUnload(loaded.ContextReference);
        var capturePipeHandle = Environment.GetEnvironmentVariable(CapturePipeEnvironmentVariable);
        Environment.SetEnvironmentVariable(CapturePipeEnvironmentVariable, null);
        if (!string.IsNullOrWhiteSpace(capturePipeHandle) &&
            !WaitForCaptureCompletion(capturePipeHandle))
        {
            WriteResult(new ProbeResult(
                false,
                loaded.AssemblyName,
                loaded.AssemblyVersion,
                unloaded,
                "capture_owner_disconnected",
                null));
            return 1;
        }

        WriteResult(new ProbeResult(
            unloaded,
            loaded.AssemblyName,
            loaded.AssemblyVersion,
            unloaded,
            unloaded ? null : "assembly_context_retained",
            null));
        return unloaded ? 0 : 1;
    }

    private static int RunResidualChildProbe(int holdMilliseconds)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            WriteResult(new ProbeResult(
                false,
                null,
                null,
                false,
                "process_path_unavailable",
                null));
            return 1;
        }

        var releaseOwnedByLifecycleHarness = string.Equals(
            Environment.GetEnvironmentVariable(ChildReleaseParentWaitEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? processPath : "/bin/sh",
            UseShellExecute = OperatingSystem.IsWindows(),
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (!OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1");
            startInfo.ArgumentList.Add(processPath);
        }

        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add("--child-hold-ms");
        startInfo.ArgumentList.Add(
            holdMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        Process? child;
        try
        {
            child = Process.Start(startInfo);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChildReleasePipeEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(
                ChildReleaseParentWaitEnvironmentVariable,
                null);
        }

        using (child)
        {
            if (child is null)
            {
                WriteResult(new ProbeResult(
                    false,
                    null,
                    null,
                    false,
                    "residual_child_start_failed",
                    null));
                return 1;
            }

            WriteResult(new ProbeResult(true, null, null, true, null, child.Id));
            if (releaseOwnedByLifecycleHarness)
            {
                if (!child.WaitForExit(10_000))
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit();
                    return 1;
                }

                return child.ExitCode;
            }

            return 0;
        }
    }

    private static bool TryReadArguments(string[] args, out string assemblyPath)
    {
        assemblyPath = string.Empty;
        if (args.Length != 2 ||
            !string.Equals(args[0], "--assembly", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[1]))
        {
            return false;
        }

        assemblyPath = args[1];
        if (!File.Exists(assemblyPath))
        {
            return false;
        }

        return true;
    }

    private static bool WaitForCaptureCompletion(string pipeHandle)
    {
        using var pipe = new System.IO.Pipes.AnonymousPipeClientStream(
            System.IO.Pipes.PipeDirection.In,
            pipeHandle);
        return pipe.ReadByte() == CaptureCompleted;
    }

    private static bool TryReadChildHoldArguments(
        string[] args,
        out int holdMilliseconds)
    {
        return TryReadBoundedMillisecondsArgument(
            args,
            "--child-hold-ms",
            out holdMilliseconds);
    }

    private static bool TryReadResidualChildArguments(
        string[] args,
        out int holdMilliseconds)
    {
        return TryReadBoundedMillisecondsArgument(
            args,
            "--spawn-residual-child-ms",
            out holdMilliseconds);
    }

    private static bool TryReadBoundedMillisecondsArgument(
        string[] args,
        string option,
        out int milliseconds)
    {
        milliseconds = 0;
        return args.Length == 2 &&
            string.Equals(args[0], option, StringComparison.Ordinal) &&
            int.TryParse(
                args[1],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out milliseconds) &&
            milliseconds is >= 25 and <= 30_000;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LoadedAssembly LoadAndRequestUnload(string assemblyPath)
    {
        var context = new ProbeLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        AppContext.SetData(TestAssemblyLoadOwnerKey, TestAssemblyLoadOwnerValue);
        try
        {
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        }
        finally
        {
            AppContext.SetData(TestAssemblyLoadOwnerKey, null);
        }

        var name = assembly.GetName();
        var result = new LoadedAssembly(
            new WeakReference(context, trackResurrection: false),
            name.Name ?? Path.GetFileNameWithoutExtension(assemblyPath),
            name.Version?.ToString());
        context.Unload();
        return result;
    }

    private static bool WaitForUnload(WeakReference contextReference)
    {
        for (var attempt = 0; contextReference.IsAlive && attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }

        return !contextReference.IsAlive;
    }

    private static void WriteResult(ProbeResult result)
    {
        Console.Out.WriteLine(
            JsonSerializer.Serialize(result, ProbeJsonContext.Default.ProbeResult));
    }

    private sealed class ProbeLoadContext(string assemblyPath)
        : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }

    private sealed record LoadedAssembly(
        WeakReference ContextReference,
        string AssemblyName,
        string? AssemblyVersion);

    internal sealed record ProbeResult(
        bool Success,
        string? AssemblyName,
        string? AssemblyVersion,
        bool Unloaded,
        string? ErrorType,
        int? ChildProcessId);
}

[JsonSerializable(typeof(Program.ProbeResult))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
