using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class SupervisorProtocolBehaviorTests
{
    public static TheoryData<byte[], int> StructuralFailures =>
        new()
        {
            { [], (int)SupervisorProtocolFailureKind.EndOfStream },
            {
                new byte[SupervisorProtocol.HeaderLength - 1],
                (int)SupervisorProtocolFailureKind.TruncatedHeader
            },
            {
                CreateFrame(
                    SupervisorProtocolEnvelopeKind.Command,
                    ValidCommandPayload,
                    magic: 0),
                (int)SupervisorProtocolFailureKind.InvalidMagic
            },
            {
                CreateFrame(
                    SupervisorProtocolEnvelopeKind.Command,
                    ValidCommandPayload,
                    version: checked((ushort)(SupervisorProtocol.Version + 1))),
                (int)SupervisorProtocolFailureKind.UnsupportedVersion
            },
            {
                CreateFrame(
                    SupervisorProtocolEnvelopeKind.Command,
                    ValidCommandPayload,
                    rawEnvelopeKind: ushort.MaxValue),
                (int)SupervisorProtocolFailureKind.UnknownEnvelopeKind
            },
            {
                CreateFrame(
                    SupervisorProtocolEnvelopeKind.Command,
                    [],
                    declaredPayloadLength: -1),
                (int)SupervisorProtocolFailureKind.InvalidPayloadLength
            },
            {
                CreateFrame(SupervisorProtocolEnvelopeKind.Command, []),
                (int)SupervisorProtocolFailureKind.InvalidPayloadLength
            },
            {
                CreateFrame(
                    SupervisorProtocolEnvelopeKind.Command,
                    [],
                    declaredPayloadLength: SupervisorProtocol.MaximumPayloadLength + 1),
                (int)SupervisorProtocolFailureKind.PayloadTooLarge
            },
            {
                CreateFrame(
                    SupervisorProtocolEnvelopeKind.Command,
                    [0x7b, 0x7d],
                    declaredPayloadLength: 10),
                (int)SupervisorProtocolFailureKind.TruncatedPayload
            }
        };

    public static TheoryData<string, int, byte[]> InvalidNumericFields
    {
        get
        {
            var data = new TheoryData<string, int, byte[]>();
            (string Name, string Json)[] invalidNumbers =
            [
                ("null", "null"),
                ("string", "\"1\""),
                ("bool", "true"),
                ("array", "[1]"),
                ("object", "{\"value\":1}"),
                ("overflow", "9223372036854775808")
            ];
            foreach (var invalid in invalidNumbers)
            {
                data.Add(
                    $"telemetrySequence-{invalid.Name}",
                    (int)SupervisorProtocolEnvelopeKind.Command,
                    Utf8($"{{\"telemetrySequence\":{invalid.Json},\"command\":1}}"));
                data.Add(
                    $"command-{invalid.Name}",
                    (int)SupervisorProtocolEnvelopeKind.Command,
                    Utf8($"{{\"telemetrySequence\":0,\"command\":{invalid.Json}}}"));
                data.Add(
                    $"status-{invalid.Name}",
                    (int)SupervisorProtocolEnvelopeKind.Status,
                    Utf8($"{{\"telemetrySequence\":0,\"status\":{invalid.Json}}}"));
                data.Add(
                    $"invariant-{invalid.Name}",
                    (int)SupervisorProtocolEnvelopeKind.Evidence,
                    Utf8(
                        $"{{\"telemetrySequence\":0,\"invariant\":{invalid.Json}," +
                        "\"state\":1,\"detail\":\"evidence\"}"));
                data.Add(
                    $"state-{invalid.Name}",
                    (int)SupervisorProtocolEnvelopeKind.Evidence,
                    Utf8(
                        "{\"telemetrySequence\":0,\"invariant\":0," +
                        $"\"state\":{invalid.Json},\"detail\":\"evidence\"}}"));
            }

            return data;
        }
    }

    public static TheoryData<int, byte[], int>
        InvalidPayloads =>
        new()
        {
            {
                (int)SupervisorProtocolEnvelopeKind.Command,
                [0xc3, 0x28],
                (int)SupervisorProtocolFailureKind.MalformedPayload
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Command,
                Utf8("{\"telemetrySequence\":0,\"command\":1} trailing"),
                (int)SupervisorProtocolFailureKind.MalformedPayload
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Command,
                Utf8("[0,1]"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Command,
                Utf8("{\"telemetrySequence\":0,\"telemetrySequence\":1,\"command\":1}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Command,
                Utf8("{\"telemetrySequence\":0,\"command\":1,\"extra\":true}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Command,
                Utf8("{\"telemetrySequence\":0,\"status\":1}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Command,
                Utf8("{\"telemetrySequence\":-1,\"command\":1}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Command,
                Utf8("{\"telemetrySequence\":0,\"command\":999}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Status,
                Utf8("{\"TelemetrySequence\":0,\"status\":1}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Evidence,
                Utf8("{\"telemetrySequence\":0,\"invariant\":0,\"state\":0,\"detail\":\"unknown\"}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Evidence,
                Utf8("{\"telemetrySequence\":0,\"invariant\":0,\"state\":1,\"detail\":\"   \"}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Evidence,
                Utf8(
                    "{\"telemetrySequence\":0,\"invariant\":0,\"state\":1,\"detail\":\"" +
                    new string('x', SupervisorProtocol.MaximumEvidenceDetailLength + 1) +
                    "\"}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Evidence,
                Utf8(
                    "{\"telemetrySequence\":0,\"invariant\":0,\"state\":1," +
                    "\"detail\":\"\\uD800\"}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            },
            {
                (int)SupervisorProtocolEnvelopeKind.Evidence,
                Utf8(
                    "{\"telemetrySequence\":0,\"invariant\":0,\"state\":1," +
                    "\"detail\":\"\\uDC00\"}"),
                (int)SupervisorProtocolFailureKind.InvalidMessage
            }
        };

    private static readonly byte[] ValidCommandPayload =
        Utf8("{\"telemetrySequence\":0,\"command\":1}");

    [Fact]
    public async Task CommandStatusAndEvidenceUseOneFramedCodec()
    {
        SupervisorProtocolMessage[] expected =
        [
            new SupervisorCommandMessage(1, SupervisorCommandKind.Begin),
            new SupervisorStatusMessage(2, SupervisorStatusKind.Accepted),
            new SupervisorEvidenceMessage(
                3,
                RequiredProcessInvariantKind.StreamDrain,
                ProcessInvariantState.Proven,
                "both streams reached EOF")
        ];
        using var stream = new MemoryStream();
        foreach (var message in expected)
        {
            Assert.IsType<SupervisorProtocolMessageWritten>(
                await SupervisorProtocolFramer.WriteAsync(
                        stream,
                        message,
                        TestContext.Current.CancellationToken)
                    .ConfigureAwait(true));
        }

        stream.Position = 0;
        foreach (var message in expected)
        {
            var read = await SupervisorProtocolFramer.ReadAsync(
                    stream,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(
                message,
                Assert.IsType<SupervisorProtocolMessageRead>(read).Message);
        }

        AssertFailure(
            await SupervisorProtocolFramer.ReadAsync(
                    stream,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true),
            SupervisorProtocolFailureKind.EndOfStream);
    }

    [Fact]
    public async Task FragmentedReadsDoNotChangeFrameMeaning()
    {
        var expected = new SupervisorStatusMessage(7, SupervisorStatusKind.Ready);
        using var encoded = new MemoryStream();
        Assert.IsType<SupervisorProtocolMessageWritten>(
            await SupervisorProtocolFramer.WriteAsync(
                    encoded,
                    expected,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true));
        using var fragmented = new FragmentedReadStream(
            encoded.ToArray(),
            maximumReadSize: 1);

        var read = await SupervisorProtocolFramer.ReadAsync(
                fragmented,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(
            expected,
            Assert.IsType<SupervisorProtocolMessageRead>(read).Message);
    }

    [Theory]
    [MemberData(nameof(StructuralFailures))]
    public async Task StructuralFailuresAreTyped(
        byte[] bytes,
        int expected)
    {
        using var stream = new FragmentedReadStream(bytes, maximumReadSize: 2);

        var read = await SupervisorProtocolFramer.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        AssertFailure(read, (SupervisorProtocolFailureKind)expected);
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task PayloadMustBeOneExactTypedShape(
        int envelopeKind,
        byte[] payload,
        int expected)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream(
            CreateFrame((SupervisorProtocolEnvelopeKind)envelopeKind, payload),
            writable: false);

        var read = await SupervisorProtocolFramer.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        AssertFailure(read, (SupervisorProtocolFailureKind)expected);
    }

    [Theory]
    [MemberData(nameof(InvalidNumericFields))]
    public async Task EveryNumericFieldRejectsWrongKindsAndOverflow(
        string caseName,
        int envelopeKind,
        byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseName);
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream(
            CreateFrame((SupervisorProtocolEnvelopeKind)envelopeKind, payload),
            writable: false);

        var read = await SupervisorProtocolFramer.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        AssertFailure(read, SupervisorProtocolFailureKind.InvalidMessage);
    }

    [Fact]
    public async Task OversizeLengthIsRejectedBeforePayloadRead()
    {
        var bytes = CreateFrame(
            SupervisorProtocolEnvelopeKind.Command,
            [1, 2, 3],
            declaredPayloadLength: SupervisorProtocol.MaximumPayloadLength + 1);
        using var stream = new MemoryStream(bytes, writable: false);

        var read = await SupervisorProtocolFramer.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        AssertFailure(read, SupervisorProtocolFailureKind.PayloadTooLarge);
        Assert.Equal(SupervisorProtocol.HeaderLength, stream.Position);
    }

    [Fact]
    public async Task InvalidTypedMessageIsRejectedBeforeStreamWrite()
    {
        using var stream = new MemoryStream();
        var invalid = new SupervisorCommandMessage(
            0,
            (SupervisorCommandKind)int.MaxValue);

        var result = await SupervisorProtocolFramer.WriteAsync(
                stream,
                invalid,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(
            SupervisorProtocolFailureKind.InvalidMessage,
            Assert.IsType<SupervisorProtocolWriteRejected>(result).Failure.Kind);
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public async Task OutboundLoneSurrogateIsRejectedBeforeStreamWrite(
        int surrogate)
    {
        using var stream = new MemoryStream();
        var invalid = new SupervisorEvidenceMessage(
            0,
            RequiredProcessInvariantKind.StreamDrain,
            ProcessInvariantState.Proven,
            new string((char)surrogate, 1));

        var result = await SupervisorProtocolFramer.WriteAsync(
                stream,
                invalid,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(
            SupervisorProtocolFailureKind.InvalidMessage,
            Assert.IsType<SupervisorProtocolWriteRejected>(result).Failure.Kind);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task ValidSurrogatePairRoundTripsWithoutReplacement()
    {
        using var stream = new MemoryStream();
        var expected = new SupervisorEvidenceMessage(
            0,
            RequiredProcessInvariantKind.StreamDrain,
            ProcessInvariantState.Proven,
            $"captured {char.ConvertFromUtf32(0x1f600)} evidence");
        Assert.IsType<SupervisorProtocolMessageWritten>(
            await SupervisorProtocolFramer.WriteAsync(
                    stream,
                    expected,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true));
        stream.Position = 0;

        var read = await SupervisorProtocolFramer.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(
            expected,
            Assert.IsType<SupervisorProtocolMessageRead>(read).Message);
    }

    [Fact]
    public async Task CancellationOnlyEndsIo()
    {
        using var stream = new CancellationStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SupervisorProtocolFramer.ReadAsync(stream, cancellation.Token)
                .ConfigureAwait(true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SupervisorProtocolFramer.WriteAsync(
                    stream,
                    new SupervisorCommandMessage(0, SupervisorCommandKind.Cancel),
                    cancellation.Token)
                .ConfigureAwait(true));
    }

    [Fact]
    public void ProtocolAssemblyHasNoHostAndExposesNoProtocolAuthority()
    {
        var assembly = typeof(SupervisorProtocolFramer).Assembly;
        var types = assembly.GetTypes();
        Assert.Null(assembly.EntryPoint);
        Assert.DoesNotContain(types, type => type.Name is
            "SupervisorHost" or
            "OwnedProcessLease" or
            "OwnedProcessCompletion");

        var protocolTypes = types.Where(type =>
            type.Name.StartsWith("SupervisorProtocol", StringComparison.Ordinal) ||
            type.Name.StartsWith("SupervisorCommand", StringComparison.Ordinal) ||
            type.Name.StartsWith("SupervisorStatus", StringComparison.Ordinal) ||
            type.Name.StartsWith("SupervisorEvidence", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(protocolTypes);
        Assert.All(protocolTypes, type =>
            Assert.False(type.IsPublic || type.IsNestedPublic, type.FullName));

        var codecOwners = protocolTypes.Where(type =>
                type.GetMethods(
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly)
                    .Any(method => method.Name is "ReadAsync" or "WriteAsync"))
            .ToArray();
        Assert.Equal(typeof(SupervisorProtocolFramer), Assert.Single(codecOwners));
        Assert.All(
            protocolTypes.Where(type => typeof(SupervisorProtocolMessage).IsAssignableFrom(type)),
            type => Assert.Null(type.GetProperty("AuthoritySequence")));
    }

    private static byte[] CreateFrame(
        SupervisorProtocolEnvelopeKind envelopeKind,
        byte[] payload,
        uint? magic = null,
        ushort? version = null,
        ushort? rawEnvelopeKind = null,
        int? declaredPayloadLength = null)
    {
        var bytes = new byte[SupervisorProtocol.HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes,
            magic ?? SupervisorProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(sizeof(uint)),
            version ?? SupervisorProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(sizeof(uint) + sizeof(ushort)),
            rawEnvelopeKind ?? (ushort)envelopeKind);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(sizeof(uint) + sizeof(ushort) + sizeof(ushort)),
            declaredPayloadLength ?? payload.Length);
        payload.CopyTo(bytes.AsSpan(SupervisorProtocol.HeaderLength));
        return bytes;
    }

    private static byte[] Utf8(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    private static void AssertFailure(
        SupervisorProtocolReadResult read,
        SupervisorProtocolFailureKind expected)
    {
        Assert.Equal(
            expected,
            Assert.IsType<SupervisorProtocolReadRejected>(read).Failure.Kind);
    }

    private sealed class FragmentedReadStream : MemoryStream
    {
        private readonly int _maximumReadSize;

        internal FragmentedReadStream(byte[] bytes, int maximumReadSize)
            : base(bytes, writable: false)
        {
            _maximumReadSize = maximumReadSize;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(
                buffer[..Math.Min(buffer.Length, _maximumReadSize)],
                cancellationToken);
        }
    }

    private sealed class CancellationStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
