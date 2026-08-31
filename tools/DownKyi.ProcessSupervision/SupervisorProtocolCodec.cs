using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownKyi.ProcessSupervision;

internal static class SupervisorProtocolCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static SupervisorProtocolEncodeResult Encode(SupervisorProtocolFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var payloadError = ValidateFrame(frame);
        if (payloadError != null)
        {
            return new SupervisorProtocolEncodeResult(null, payloadError);
        }

        var payload = SerializePayload(frame);
        if (payload.Length > SupervisorProtocol.MaximumPayloadLength)
        {
            return RejectEncoding(
                SupervisorProtocolErrorKind.PayloadTooLarge,
                "The supervision payload exceeds the protocol maximum.");
        }

        var bytes = new byte[SupervisorProtocol.HeaderLength + payload.Length];
        var header = bytes.AsSpan(0, SupervisorProtocol.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, SupervisorProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[sizeof(uint)..], SupervisorProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header[(sizeof(uint) + sizeof(ushort))..],
            (ushort)frame.Kind);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[(sizeof(uint) + sizeof(ushort) + sizeof(ushort))..],
            payload.Length);
        payload.CopyTo(bytes.AsSpan(SupervisorProtocol.HeaderLength));
        return new SupervisorProtocolEncodeResult(bytes, null);
    }

    internal static async ValueTask<SupervisorProtocolError?> WriteAsync(
        Stream stream,
        SupervisorProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var encoded = Encode(frame);
        if (!encoded.Succeeded)
        {
            return encoded.Error;
        }

        await stream.WriteAsync(encoded.Bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    internal static async ValueTask<SupervisorProtocolReadResult> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var headerRead = await ReadExactOrEofAsync(
                stream,
                SupervisorProtocol.HeaderLength,
                cancellationToken)
            .ConfigureAwait(false);
        if (headerRead.BytesRead == 0)
        {
            return new SupervisorProtocolChannelClosed();
        }
        if (headerRead.BytesRead != SupervisorProtocol.HeaderLength)
        {
            return RejectRead(
                SupervisorProtocolErrorKind.TruncatedHeader,
                "The supervision channel closed during a frame header.");
        }

        var header = headerRead.Buffer.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != SupervisorProtocol.Magic)
        {
            return RejectRead(
                SupervisorProtocolErrorKind.BadMagic,
                "The supervision frame magic is invalid.");
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[sizeof(uint)..]) !=
            SupervisorProtocol.Version)
        {
            return RejectRead(
                SupervisorProtocolErrorKind.UnsupportedVersion,
                "The supervision protocol version is unsupported.");
        }

        var kindValue = BinaryPrimitives.ReadUInt16LittleEndian(
            header[(sizeof(uint) + sizeof(ushort))..]);
        if (!Enum.IsDefined(typeof(SupervisorProtocolKind), kindValue))
        {
            return RejectRead(
                SupervisorProtocolErrorKind.UnknownKind,
                "The supervision frame kind is unknown.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            header[(sizeof(uint) + sizeof(ushort) + sizeof(ushort))..]);
        if (payloadLength < 0)
        {
            return RejectRead(
                SupervisorProtocolErrorKind.InvalidPayloadLength,
                "The supervision payload length is invalid.");
        }
        if (payloadLength > SupervisorProtocol.MaximumPayloadLength)
        {
            return RejectRead(
                SupervisorProtocolErrorKind.PayloadTooLarge,
                "The supervision payload exceeds the protocol maximum.");
        }

        var payloadRead = await ReadExactOrEofAsync(stream, payloadLength, cancellationToken)
            .ConfigureAwait(false);
        if (payloadRead.BytesRead != payloadLength)
        {
            return RejectRead(
                SupervisorProtocolErrorKind.TruncatedPayload,
                "The supervision channel closed during a frame payload.");
        }

        return DecodePayload((SupervisorProtocolKind)kindValue, payloadRead.Buffer);
    }

    private static SupervisorProtocolReadResult DecodePayload(
        SupervisorProtocolKind kind,
        byte[] payload)
    {
        try
        {
            SupervisorProtocolFrame? frame = kind switch
            {
                SupervisorProtocolKind.AttachOwnership => new AttachOwnershipFrame(
                    Deserialize<ContainmentAttachment>(payload)),
                SupervisorProtocolKind.OwnershipReady => new OwnershipReadyFrame(
                    Deserialize<ProcessOwnershipMetadata>(payload)),
                SupervisorProtocolKind.AuthorizeLaunch => DecodeLaunchSpecFrame(payload),
                SupervisorProtocolKind.TargetStarted => new TargetStartedFrame(
                    Deserialize<TargetStarted>(payload)),
                SupervisorProtocolKind.TargetExited => new TargetExitedFrame(
                    Deserialize<TargetExited>(payload)),
                SupervisorProtocolKind.Finalize when payload.Length == 0 => new FinalizeFrame(),
                SupervisorProtocolKind.Finalized when payload.Length == 0 => new FinalizedFrame(),
                _ => null
            };
            if (frame == null)
            {
                return RejectRead(
                    SupervisorProtocolErrorKind.InvalidPayload,
                    "The supervision frame payload is invalid for its kind.");
            }

            var validationError = ValidateFrame(frame);
            return validationError == null
                ? new SupervisorProtocolFrameRead(frame)
                : new SupervisorProtocolReadRejected(validationError);
        }
        catch (JsonException)
        {
            return RejectRead(
                SupervisorProtocolErrorKind.InvalidPayload,
                "The supervision frame payload is not valid typed JSON.");
        }
        catch (NotSupportedException)
        {
            return RejectRead(
                SupervisorProtocolErrorKind.InvalidPayload,
                "The supervision frame payload type is unsupported.");
        }
        catch (ArgumentException)
        {
            return RejectRead(
                SupervisorProtocolErrorKind.InvalidPayload,
                "The supervision frame payload omitted a required typed value.");
        }
    }

    private static T Deserialize<T>(byte[] payload)
    {
        if (payload.Length == 0)
        {
            throw new JsonException("A typed supervision payload cannot be empty.");
        }

        return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
            ?? throw new JsonException("The typed supervision payload was null.");
    }

    private static byte[] SerializePayload(SupervisorProtocolFrame frame)
    {
        return frame switch
        {
            AttachOwnershipFrame value => JsonSerializer.SerializeToUtf8Bytes(
                value.Attachment,
                SerializerOptions),
            OwnershipReadyFrame value => JsonSerializer.SerializeToUtf8Bytes(
                value.Ownership,
                SerializerOptions),
            AuthorizeLaunchFrame value => JsonSerializer.SerializeToUtf8Bytes(
                LaunchSpecTransport.FromLaunchSpec(value.LaunchSpec),
                SerializerOptions),
            TargetStartedFrame value => JsonSerializer.SerializeToUtf8Bytes(
                value.Started,
                SerializerOptions),
            TargetExitedFrame value => JsonSerializer.SerializeToUtf8Bytes(
                value.Exited,
                SerializerOptions),
            FinalizeFrame or FinalizedFrame => [],
            _ => throw new InvalidOperationException("The supervision frame type is unknown.")
        };
    }

    private static SupervisorProtocolError? ValidateFrame(SupervisorProtocolFrame frame)
    {
        var valid = frame switch
        {
            AttachOwnershipFrame value => IsValid(value.Attachment),
            OwnershipReadyFrame value => IsValid(value.Ownership),
            AuthorizeLaunchFrame value => value.LaunchSpec != null,
            TargetStartedFrame value => value.Started.ProcessId > 0,
            TargetExitedFrame value => value.Exited != null,
            FinalizeFrame or FinalizedFrame => true,
            _ => false
        };
        return valid
            ? null
            : new SupervisorProtocolError(
                SupervisorProtocolErrorKind.InvalidPayload,
                "The supervision frame contains an invalid typed payload.");
    }

    private static bool IsValid(ContainmentAttachment value)
    {
        return value != null &&
               Enum.IsDefined(value.BackendKind) &&
               !string.IsNullOrWhiteSpace(value.ContainmentId) &&
               !string.IsNullOrWhiteSpace(value.MembershipId) &&
               !string.IsNullOrWhiteSpace(value.OwnerLifetimeId);
    }

    private static bool IsValid(ProcessOwnershipMetadata value)
    {
        return value != null &&
               value.OwnershipEstablished &&
               !string.IsNullOrWhiteSpace(value.ContainmentId) &&
               !string.IsNullOrWhiteSpace(value.MembershipId) &&
               !string.IsNullOrWhiteSpace(value.OwnerLifetimeId);
    }

    private static AuthorizeLaunchFrame DecodeLaunchSpecFrame(byte[] payload)
    {
        var transport = Deserialize<LaunchSpecTransport>(payload);
        if (string.IsNullOrWhiteSpace(transport.FileName) ||
            string.IsNullOrWhiteSpace(transport.WorkingDirectory) ||
            transport.Arguments == null ||
            transport.Arguments.Any(argument => argument == null) ||
            transport.Environment?.Keys.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new ArgumentException("The launch authorization payload is incomplete.");
        }

        return new AuthorizeLaunchFrame(new LaunchSpec(
            transport.FileName,
            transport.Arguments.Select(argument => argument!),
            transport.WorkingDirectory,
            transport.Environment,
            transport.CloseStandardInput));
    }

    private static SupervisorProtocolEncodeResult RejectEncoding(
        SupervisorProtocolErrorKind kind,
        string message)
    {
        return new SupervisorProtocolEncodeResult(null, new SupervisorProtocolError(kind, message));
    }

    private static SupervisorProtocolReadRejected RejectRead(
        SupervisorProtocolErrorKind kind,
        string message)
    {
        return new SupervisorProtocolReadRejected(new SupervisorProtocolError(kind, message));
    }

    private static async ValueTask<ExactReadResult> ReadExactOrEofAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(offset, length - offset),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return new ExactReadResult(buffer, offset);
    }

    private sealed record ExactReadResult(byte[] Buffer, int BytesRead);

    private sealed record LaunchSpecTransport(
        string? FileName,
        IReadOnlyList<string?>? Arguments,
        string? WorkingDirectory,
        IReadOnlyDictionary<string, string?>? Environment,
        bool CloseStandardInput)
    {
        internal static LaunchSpecTransport FromLaunchSpec(LaunchSpec launchSpec)
        {
            return new LaunchSpecTransport(
                launchSpec.FileName,
                launchSpec.Arguments,
                launchSpec.WorkingDirectory,
                launchSpec.Environment,
                launchSpec.CloseStandardInput);
        }
    }
}
