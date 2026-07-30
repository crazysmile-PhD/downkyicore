using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownKyi.AssemblyLifecycleProbe;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (TryReadChildHoldArguments(args, out var childHoldMilliseconds))
        {
            Thread.Sleep(childHoldMilliseconds);
            return 0;
        }

        if (TryReadResidualChildArguments(args, out var residualChildHoldMilliseconds))
        {
            return RunResidualChildProbe(residualChildHoldMilliseconds);
        }

        if (!TryReadArguments(args, out var assemblyPath, out var holdAfterUnloadMilliseconds))
        {
            WriteResult(new ProbeResult(false, null, null, false, "invalid_arguments", null));
            return 2;
        }

        var fullPath = Path.GetFullPath(assemblyPath);
        var loaded = LoadAndRequestUnload(fullPath);
        var unloaded = WaitForUnload(loaded.ContextReference);
        if (holdAfterUnloadMilliseconds > 0)
        {
            Thread.Sleep(holdAfterUnloadMilliseconds);
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

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add("--child-hold-ms");
        startInfo.ArgumentList.Add(
            holdMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        using var child = Process.Start(startInfo);
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
        return 0;
    }

    private static bool TryReadArguments(
        string[] args,
        out string assemblyPath,
        out int holdAfterUnloadMilliseconds)
    {
        assemblyPath = string.Empty;
        holdAfterUnloadMilliseconds = 0;
        if (args.Length is not (2 or 4) ||
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

        if (args.Length == 2)
        {
            return true;
        }

        return string.Equals(args[2], "--hold-after-unload-ms", StringComparison.Ordinal) &&
            int.TryParse(
                args[3],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out holdAfterUnloadMilliseconds) &&
            holdAfterUnloadMilliseconds is >= 0 and <= 30_000;
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
            milliseconds is >= 1_000 and <= 30_000;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LoadedAssembly LoadAndRequestUnload(string assemblyPath)
    {
        var context = new ProbeLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

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
