namespace DownKyi.ProcessSupervision;

internal static class SupervisorProtocol
{
    internal const uint Magic = 0x31505344; // DSP1 in little-endian byte order.
    internal const ushort Version = 1;
    internal const int HeaderLength =
        sizeof(uint) + sizeof(ushort) + sizeof(ushort) + sizeof(int);
    internal const int MaximumPayloadLength = 64 * 1024;
    internal const int MaximumEvidenceDetailLength = 1024;
}

internal enum SupervisorProtocolEnvelopeKind : ushort
{
    Command = 1,
    Status = 2,
    Evidence = 3
}

internal enum SupervisorCommandKind
{
    Begin = 1,
    Cancel = 2,
    Finalize = 3
}

internal enum SupervisorStatusKind
{
    Ready = 1,
    Accepted = 2,
    Rejected = 3,
    Finished = 4
}

internal abstract record SupervisorProtocolMessage(long TelemetrySequence)
{
    // Transport telemetry only. Lifecycle arbitration remains with the future
    // process owner and its AuthoritySequence contract.
}

internal sealed record SupervisorCommandMessage(
    long TelemetrySequence,
    SupervisorCommandKind Command)
    : SupervisorProtocolMessage(TelemetrySequence);

internal sealed record SupervisorStatusMessage(
    long TelemetrySequence,
    SupervisorStatusKind Status)
    : SupervisorProtocolMessage(TelemetrySequence);

internal sealed record SupervisorEvidenceMessage(
    long TelemetrySequence,
    RequiredProcessInvariantKind Invariant,
    ProcessInvariantState State,
    string Detail)
    : SupervisorProtocolMessage(TelemetrySequence);

internal enum SupervisorProtocolFailureKind
{
    EndOfStream,
    TruncatedHeader,
    InvalidMagic,
    UnsupportedVersion,
    UnknownEnvelopeKind,
    InvalidPayloadLength,
    PayloadTooLarge,
    TruncatedPayload,
    MalformedPayload,
    InvalidMessage,
    TransportFailure
}

internal sealed record SupervisorProtocolFailure(
    SupervisorProtocolFailureKind Kind,
    string Detail);

internal abstract record SupervisorProtocolReadResult;

internal sealed record SupervisorProtocolMessageRead(
    SupervisorProtocolMessage Message)
    : SupervisorProtocolReadResult;

internal sealed record SupervisorProtocolReadRejected(
    SupervisorProtocolFailure Failure)
    : SupervisorProtocolReadResult;

internal abstract record SupervisorProtocolWriteResult;

internal sealed record SupervisorProtocolMessageWritten()
    : SupervisorProtocolWriteResult;

internal sealed record SupervisorProtocolWriteRejected(
    SupervisorProtocolFailure Failure)
    : SupervisorProtocolWriteResult;

internal sealed record SupervisorProtocolEncodeResult(
    SupervisorProtocolEnvelopeKind? EnvelopeKind,
    byte[]? Payload,
    SupervisorProtocolFailure? Failure)
{
    internal bool Succeeded =>
        EnvelopeKind.HasValue && Payload is not null && Failure is null;
}
