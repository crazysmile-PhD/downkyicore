using System.Text.Json;

namespace DownKyi.ProcessSupervision;

internal static class SupervisorProtocolValidator
{
    internal const string TelemetrySequenceProperty = "telemetrySequence";
    internal const string CommandProperty = "command";
    internal const string StatusProperty = "status";
    internal const string InvariantProperty = "invariant";
    internal const string StateProperty = "state";
    internal const string DetailProperty = "detail";

    internal static SupervisorProtocolFailure? ValidateForWrite(
        SupervisorProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.TelemetrySequence < 0)
        {
            return Invalid("Telemetry sequence must be non-negative.");
        }

        return message switch
        {
            SupervisorCommandMessage command when Enum.IsDefined(command.Command) => null,
            SupervisorStatusMessage status when Enum.IsDefined(status.Status) => null,
            SupervisorEvidenceMessage evidence when
                Enum.IsDefined(evidence.Invariant) &&
                evidence.State is ProcessInvariantState.Proven or
                    ProcessInvariantState.Violated &&
                !string.IsNullOrWhiteSpace(evidence.Detail) &&
                evidence.Detail.Length <= SupervisorProtocol.MaximumEvidenceDetailLength &&
                HasValidSurrogatePairs(evidence.Detail) => null,
            _ => Invalid(
                "The message contains an unknown kind or invalid evidence value.")
        };
    }

    internal static SupervisorProtocolEnvelopeKind GetEnvelopeKind(
        SupervisorProtocolMessage message)
    {
        return message switch
        {
            SupervisorCommandMessage => SupervisorProtocolEnvelopeKind.Command,
            SupervisorStatusMessage => SupervisorProtocolEnvelopeKind.Status,
            SupervisorEvidenceMessage => SupervisorProtocolEnvelopeKind.Evidence,
            _ => throw new InvalidOperationException(
                "The validated supervisor message type is unsupported.")
        };
    }

    internal static SupervisorProtocolReadResult Decode(
        SupervisorProtocolEnvelopeKind envelopeKind,
        JsonElement root)
    {
        return envelopeKind switch
        {
            SupervisorProtocolEnvelopeKind.Command => DecodeCommand(root),
            SupervisorProtocolEnvelopeKind.Status => DecodeStatus(root),
            SupervisorProtocolEnvelopeKind.Evidence => DecodeEvidence(root),
            _ => Reject(
                SupervisorProtocolFailureKind.UnknownEnvelopeKind,
                "The envelope kind is not defined by this protocol version.")
        };
    }

    private static SupervisorProtocolReadResult DecodeCommand(JsonElement root)
    {
        if (!HasExactProperties(root, TelemetrySequenceProperty, CommandProperty) ||
            !TryReadTelemetrySequence(root, out var sequence) ||
            !TryReadDefinedEnum(root, CommandProperty, out SupervisorCommandKind command))
        {
            return InvalidMessage(
                "A command requires exactly telemetrySequence and command.");
        }

        return Accept(new SupervisorCommandMessage(sequence, command));
    }

    private static SupervisorProtocolReadResult DecodeStatus(JsonElement root)
    {
        if (!HasExactProperties(root, TelemetrySequenceProperty, StatusProperty) ||
            !TryReadTelemetrySequence(root, out var sequence) ||
            !TryReadDefinedEnum(root, StatusProperty, out SupervisorStatusKind status))
        {
            return InvalidMessage(
                "A status requires exactly telemetrySequence and status.");
        }

        return Accept(new SupervisorStatusMessage(sequence, status));
    }

    private static SupervisorProtocolReadResult DecodeEvidence(JsonElement root)
    {
        if (!HasExactProperties(
                root,
                TelemetrySequenceProperty,
                InvariantProperty,
                StateProperty,
                DetailProperty) ||
            !TryReadTelemetrySequence(root, out var sequence) ||
            !TryReadDefinedEnum(
                root,
                InvariantProperty,
                out RequiredProcessInvariantKind invariant) ||
            !TryReadDefinedEnum(root, StateProperty, out ProcessInvariantState state) ||
            !TryReadDetail(root, out var detail))
        {
            return InvalidMessage(
                "Evidence requires exactly telemetrySequence, invariant, state, and detail.");
        }

        var message = new SupervisorEvidenceMessage(
            sequence,
            invariant,
            state,
            detail);
        var failure = ValidateForWrite(message);
        return failure is null
            ? Accept(message)
            : new SupervisorProtocolReadRejected(failure);
    }

    private static bool HasExactProperties(
        JsonElement root,
        params string[] expectedProperties)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var expected = expectedProperties.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return seen.SetEquals(expected);
    }

    private static bool TryReadTelemetrySequence(
        JsonElement root,
        out long sequence)
    {
        var property = root.GetProperty(TelemetrySequenceProperty);
        sequence = default;
        return property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out sequence) &&
               sequence >= 0;
    }

    private static bool TryReadDefinedEnum<TEnum>(
        JsonElement root,
        string propertyName,
        out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        var property = root.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var raw))
        {
            return false;
        }

        var candidate = (TEnum)Enum.ToObject(typeof(TEnum), raw);
        if (!Enum.IsDefined(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryReadDetail(
        JsonElement root,
        out string detail)
    {
        var property = root.GetProperty(DetailProperty);
        detail = property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
        return !string.IsNullOrWhiteSpace(detail) &&
               detail.Length <= SupervisorProtocol.MaximumEvidenceDetailLength;
    }

    private static bool HasValidSurrogatePairs(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }

            if (!char.IsHighSurrogate(value[index]))
            {
                continue;
            }

            if (index + 1 >= value.Length ||
                !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static SupervisorProtocolMessageRead Accept(
        SupervisorProtocolMessage message)
    {
        return new SupervisorProtocolMessageRead(message);
    }

    private static SupervisorProtocolReadRejected InvalidMessage(string detail)
    {
        return new SupervisorProtocolReadRejected(Invalid(detail));
    }

    private static SupervisorProtocolFailure Invalid(string detail)
    {
        return new SupervisorProtocolFailure(
            SupervisorProtocolFailureKind.InvalidMessage,
            detail);
    }

    private static SupervisorProtocolReadRejected Reject(
        SupervisorProtocolFailureKind kind,
        string detail)
    {
        return new SupervisorProtocolReadRejected(
            new SupervisorProtocolFailure(kind, detail));
    }
}
