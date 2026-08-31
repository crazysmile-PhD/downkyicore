namespace DownKyi.ProcessSupervision;

internal static class SupervisorProtocol
{
    internal const uint Magic = 0x50534B44; // DKSP in little-endian byte order.
    internal const ushort Version = 1;
    internal const int HeaderLength = sizeof(uint) + sizeof(ushort) + sizeof(ushort) + sizeof(int);
    internal const int MaximumPayloadLength = 1024 * 1024;
}

internal enum SupervisorProtocolKind : ushort
{
    AttachOwnership = 1,
    OwnershipReady = 2,
    AuthorizeLaunch = 3,
    TargetStarted = 4,
    TargetExited = 5,
    Finalize = 6,
    Finalized = 7
}

internal abstract record SupervisorProtocolFrame(SupervisorProtocolKind Kind);

internal sealed record AttachOwnershipFrame(ContainmentAttachment Attachment)
    : SupervisorProtocolFrame(SupervisorProtocolKind.AttachOwnership);

internal sealed record OwnershipReadyFrame(ProcessOwnershipMetadata Ownership)
    : SupervisorProtocolFrame(SupervisorProtocolKind.OwnershipReady);

internal sealed record AuthorizeLaunchFrame(LaunchSpec LaunchSpec)
    : SupervisorProtocolFrame(SupervisorProtocolKind.AuthorizeLaunch);

internal sealed record TargetStartedFrame(TargetStarted Started)
    : SupervisorProtocolFrame(SupervisorProtocolKind.TargetStarted);

internal sealed record TargetExitedFrame(TargetExited Exited)
    : SupervisorProtocolFrame(SupervisorProtocolKind.TargetExited);

internal sealed record FinalizeFrame()
    : SupervisorProtocolFrame(SupervisorProtocolKind.Finalize);

internal sealed record FinalizedFrame()
    : SupervisorProtocolFrame(SupervisorProtocolKind.Finalized);

internal sealed record TargetStarted(int ProcessId);

internal sealed record TargetExited(int ExitCode);

internal enum SupervisorProtocolErrorKind
{
    TruncatedHeader,
    BadMagic,
    UnsupportedVersion,
    UnknownKind,
    InvalidPayloadLength,
    PayloadTooLarge,
    TruncatedPayload,
    InvalidPayload,
    UnexpectedFrame
}

internal sealed record SupervisorProtocolError(
    SupervisorProtocolErrorKind Kind,
    string Message,
    SupervisorProtocolKind? ExpectedKind = null,
    SupervisorProtocolKind? ActualKind = null);

internal abstract record SupervisorProtocolReadResult;

internal sealed record SupervisorProtocolFrameRead(SupervisorProtocolFrame Frame)
    : SupervisorProtocolReadResult;

internal sealed record SupervisorProtocolChannelClosed()
    : SupervisorProtocolReadResult;

internal sealed record SupervisorProtocolReadRejected(SupervisorProtocolError Error)
    : SupervisorProtocolReadResult;

internal sealed record SupervisorProtocolEncodeResult(
    byte[]? Bytes,
    SupervisorProtocolError? Error)
{
    internal bool Succeeded => Bytes != null && Error == null;
}
