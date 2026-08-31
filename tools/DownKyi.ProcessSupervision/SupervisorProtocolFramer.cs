using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace DownKyi.ProcessSupervision;

internal static class SupervisorProtocolFramer
{
    internal static async ValueTask<SupervisorProtocolWriteResult> WriteAsync(
        Stream stream,
        SupervisorProtocolMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var encoded = Encode(message);
        if (!encoded.Succeeded)
        {
            return new SupervisorProtocolWriteRejected(encoded.Failure!);
        }

        var header = CreateHeader(
            encoded.EnvelopeKind!.Value,
            encoded.Payload!.Length);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(encoded.Payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new SupervisorProtocolMessageWritten();
        }
        catch (IOException exception)
        {
            return RejectWriteTransport(exception);
        }
        catch (ObjectDisposedException exception)
        {
            return RejectWriteTransport(exception);
        }
        catch (NotSupportedException exception)
        {
            return RejectWriteTransport(exception);
        }
    }

    internal static async ValueTask<SupervisorProtocolReadResult> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            var header = new byte[SupervisorProtocol.HeaderLength];
            var headerBytes = await ReadExactOrEofAsync(
                    stream,
                    header,
                    cancellationToken)
                .ConfigureAwait(false);
            if (headerBytes == 0)
            {
                return RejectRead(
                    SupervisorProtocolFailureKind.EndOfStream,
                    "The stream ended before the next frame header.");
            }

            if (headerBytes != header.Length)
            {
                return RejectRead(
                    SupervisorProtocolFailureKind.TruncatedHeader,
                    "The stream ended during a frame header.");
            }

            var headerFailure = ValidateHeader(
                header,
                out var envelopeKind,
                out var payloadLength);
            if (headerFailure is not null)
            {
                return new SupervisorProtocolReadRejected(headerFailure);
            }

            var payload = new byte[payloadLength];
            var payloadBytes = await ReadExactOrEofAsync(
                    stream,
                    payload,
                    cancellationToken)
                .ConfigureAwait(false);
            if (payloadBytes != payloadLength)
            {
                return RejectRead(
                    SupervisorProtocolFailureKind.TruncatedPayload,
                    "The stream ended during a frame payload.");
            }

            return Decode(envelopeKind, payload);
        }
        catch (IOException exception)
        {
            return RejectReadTransport(exception);
        }
        catch (ObjectDisposedException exception)
        {
            return RejectReadTransport(exception);
        }
        catch (NotSupportedException exception)
        {
            return RejectReadTransport(exception);
        }
    }

    private static byte[] CreateHeader(
        SupervisorProtocolEnvelopeKind envelopeKind,
        int payloadLength)
    {
        var header = new byte[SupervisorProtocol.HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, SupervisorProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(sizeof(uint)),
            SupervisorProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(sizeof(uint) + sizeof(ushort)),
            (ushort)envelopeKind);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(sizeof(uint) + sizeof(ushort) + sizeof(ushort)),
            payloadLength);
        return header;
    }

    private static SupervisorProtocolFailure? ValidateHeader(
        byte[] header,
        out SupervisorProtocolEnvelopeKind envelopeKind,
        out int payloadLength)
    {
        envelopeKind = default;
        payloadLength = 0;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != SupervisorProtocol.Magic)
        {
            return Failure(
                SupervisorProtocolFailureKind.InvalidMagic,
                "The frame magic is invalid.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(sizeof(uint)));
        if (version != SupervisorProtocol.Version)
        {
            return Failure(
                SupervisorProtocolFailureKind.UnsupportedVersion,
                "The frame version is unsupported.");
        }

        var rawEnvelopeKind = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(sizeof(uint) + sizeof(ushort)));
        if (!Enum.IsDefined(typeof(SupervisorProtocolEnvelopeKind), rawEnvelopeKind))
        {
            return Failure(
                SupervisorProtocolFailureKind.UnknownEnvelopeKind,
                "The frame envelope kind is unknown.");
        }

        payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            header.AsSpan(sizeof(uint) + sizeof(ushort) + sizeof(ushort)));
        if (payloadLength < 0)
        {
            return Failure(
                SupervisorProtocolFailureKind.InvalidPayloadLength,
                "The frame payload length is negative.");
        }

        if (payloadLength == 0)
        {
            return Failure(
                SupervisorProtocolFailureKind.InvalidPayloadLength,
                "The frame payload length must be positive.");
        }

        if (payloadLength > SupervisorProtocol.MaximumPayloadLength)
        {
            return Failure(
                SupervisorProtocolFailureKind.PayloadTooLarge,
                "The frame payload exceeds the protocol limit.");
        }

        envelopeKind = (SupervisorProtocolEnvelopeKind)rawEnvelopeKind;
        return null;
    }

    private static SupervisorProtocolEncodeResult Encode(
        SupervisorProtocolMessage message)
    {
        var failure = SupervisorProtocolValidator.ValidateForWrite(message);
        if (failure is not null)
        {
            return new SupervisorProtocolEncodeResult(null, null, failure);
        }

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber(
                SupervisorProtocolValidator.TelemetrySequenceProperty,
                message.TelemetrySequence);
            WriteMessageBody(writer, message);
            writer.WriteEndObject();
        }

        var payload = output.WrittenSpan.ToArray();
        if (payload.Length > SupervisorProtocol.MaximumPayloadLength)
        {
            return new SupervisorProtocolEncodeResult(
                null,
                null,
                Failure(
                    SupervisorProtocolFailureKind.PayloadTooLarge,
                    "The encoded payload exceeds the protocol limit."));
        }

        return new SupervisorProtocolEncodeResult(
            SupervisorProtocolValidator.GetEnvelopeKind(message),
            payload,
            null);
    }

    private static void WriteMessageBody(
        Utf8JsonWriter writer,
        SupervisorProtocolMessage message)
    {
        switch (message)
        {
            case SupervisorCommandMessage command:
                writer.WriteNumber(
                    SupervisorProtocolValidator.CommandProperty,
                    (int)command.Command);
                break;
            case SupervisorStatusMessage status:
                writer.WriteNumber(
                    SupervisorProtocolValidator.StatusProperty,
                    (int)status.Status);
                break;
            case SupervisorEvidenceMessage evidence:
                writer.WriteNumber(
                    SupervisorProtocolValidator.InvariantProperty,
                    (int)evidence.Invariant);
                writer.WriteNumber(
                    SupervisorProtocolValidator.StateProperty,
                    (int)evidence.State);
                writer.WriteString(
                    SupervisorProtocolValidator.DetailProperty,
                    evidence.Detail);
                break;
            default:
                throw new InvalidOperationException(
                    "The validated supervisor message type is unsupported.");
        }
    }

    private static SupervisorProtocolReadResult Decode(
        SupervisorProtocolEnvelopeKind envelopeKind,
        byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            return SupervisorProtocolValidator.Decode(
                envelopeKind,
                document.RootElement);
        }
        catch (JsonException)
        {
            return RejectRead(
                SupervisorProtocolFailureKind.MalformedPayload,
                "The payload is not one complete UTF-8 JSON value.");
        }
        catch (InvalidOperationException)
        {
            return RejectRead(
                SupervisorProtocolFailureKind.InvalidMessage,
                "The payload cannot be projected to the declared message shape.");
        }
        catch (ArgumentException)
        {
            return RejectRead(
                SupervisorProtocolFailureKind.InvalidMessage,
                "The payload contains an invalid typed value.");
        }
    }

    private static async ValueTask<int> ReadExactOrEofAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(offset),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }

    private static SupervisorProtocolFailure Failure(
        SupervisorProtocolFailureKind kind,
        string detail)
    {
        return new SupervisorProtocolFailure(kind, detail);
    }

    private static SupervisorProtocolReadRejected RejectRead(
        SupervisorProtocolFailureKind kind,
        string detail)
    {
        return new SupervisorProtocolReadRejected(Failure(kind, detail));
    }

    private static SupervisorProtocolReadRejected RejectReadTransport(
        Exception exception)
    {
        return RejectRead(
            SupervisorProtocolFailureKind.TransportFailure,
            $"The transport failed with {exception.GetType().Name}.");
    }

    private static SupervisorProtocolWriteRejected RejectWriteTransport(
        Exception exception)
    {
        return new SupervisorProtocolWriteRejected(Failure(
            SupervisorProtocolFailureKind.TransportFailure,
            $"The transport failed with {exception.GetType().Name}."));
    }
}
