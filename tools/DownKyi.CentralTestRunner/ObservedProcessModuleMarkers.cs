namespace DownKyi.CentralTestRunner;

internal enum ObservedProcessModule : byte
{
    GetProcessById = 1,
    IdentityRead = 2,
    HasExitedFallback = 3,
    PidPresenceFallback = 4,
    NativeStateProbe = 5,
    WaitForExit = 6,
}

internal struct ObservedProcessModuleMarkers
{
    private byte hasExitedResult;
    private byte pidPresenceResult;

    internal ObservedProcessModuleMarkers(int processId)
    {
        ProcessId = processId;
    }

    internal int ProcessId { get; }

    internal ulong EnteredMask { get; private set; }

    internal ulong CompletedMask { get; private set; }

    internal byte LastModule { get; private set; }

    internal void Enter(ObservedProcessModule module)
    {
        var moduleNumber = (byte)module;
        EnteredMask |= 1UL << (moduleNumber - 1);
        LastModule = moduleNumber;
    }

    internal void Complete(ObservedProcessModule module)
    {
        CompletedMask |= 1UL << ((byte)module - 1);
    }

    internal void ObserveHasExited(bool value)
    {
        hasExitedResult = Encode(value);
    }

    internal void ObservePidPresence(bool value)
    {
        pidPresenceResult = Encode(value);
    }

    internal string FormatFailure(Exception exception, bool cancellationRequested)
    {
        return $"targetPid={ProcessId} " +
            $"enteredMask=0x{EnteredMask:X} completedMask=0x{CompletedMask:X} " +
            $"lastModule={FormatModule(LastModule)} firstIncomplete={FindFirstIncompleteModule()} " +
            $"exception={exception.GetType().FullName} cancellationRequested={cancellationRequested} " +
            $"hasExited={Decode(hasExitedResult)} pidPresence={Decode(pidPresenceResult)} " +
            $"modules=[{FormatState(ObservedProcessModule.GetProcessById)}," +
            $"{FormatState(ObservedProcessModule.IdentityRead)}," +
            $"{FormatState(ObservedProcessModule.HasExitedFallback)}," +
            $"{FormatState(ObservedProcessModule.PidPresenceFallback)}," +
            $"{FormatState(ObservedProcessModule.NativeStateProbe)}," +
            $"{FormatState(ObservedProcessModule.WaitForExit)}]";
    }

    private readonly string FindFirstIncompleteModule()
    {
        for (byte module = 1; module <= (byte)ObservedProcessModule.WaitForExit; module++)
        {
            var bit = 1UL << (module - 1);
            if ((EnteredMask & bit) != 0 && (CompletedMask & bit) == 0)
            {
                return FormatModule(module);
            }
        }

        return "none";
    }

    private readonly string FormatState(ObservedProcessModule module)
    {
        var bit = 1UL << ((byte)module - 1);
        var entered = (EnteredMask & bit) != 0;
        var completed = (CompletedMask & bit) != 0;
        var state = entered
            ? completed ? "entered/completed" : "entered/not-completed"
            : "not-invoked";
        return $"{FormatModule((byte)module)}={state}";
    }

    private static byte Encode(bool value)
    {
        return value ? (byte)2 : (byte)1;
    }

    private static string Decode(byte value)
    {
        return value switch
        {
            1 => "false",
            2 => "true",
            _ => "unavailable",
        };
    }

    private static string FormatModule(byte module)
    {
        return module == 0 ? "none" : $"M{module}";
    }
}
