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
    private const int CaptureCompleted = 0xA5;

    public static int Main(string[] args)
    {
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
