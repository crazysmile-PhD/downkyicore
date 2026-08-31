using System.Buffers.Binary;
using DownKyi.ProcessSupervision;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class SupervisorProtocolCodecTests
{
    [Fact]
    public async Task FragmentedTypedFrameDecodesWithoutDependingOnReadBoundaries()
    {
        var expected = new AttachOwnershipFrame(
            new ContainmentAttachment(
                ProcessContainmentBackendKind.WindowsJob,
                "containment",
                "membership",
                "owner"));
        using var stream = new FragmentedReadStream(Encode(expected), maximumReadSize: 1);

        var read = await SupervisorProtocolCodec.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var frame = Assert.IsType<AttachOwnershipFrame>(
            Assert.IsType<SupervisorProtocolFrameRead>(read).Frame);
        Assert.Equal(expected.Attachment, frame.Attachment);
    }

    [Fact]
    public async Task CoalescedZeroPayloadFramesRemainDistinct()
    {
        var bytes = Encode(new FinalizeFrame()).Concat(Encode(new FinalizedFrame())).ToArray();
        using var stream = new MemoryStream(bytes, writable: false);

        var first = await SupervisorProtocolCodec.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var second = await SupervisorProtocolCodec.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.IsType<FinalizeFrame>(Assert.IsType<SupervisorProtocolFrameRead>(first).Frame);
        Assert.IsType<FinalizedFrame>(Assert.IsType<SupervisorProtocolFrameRead>(second).Frame);
    }

    [Theory]
    [InlineData(SupervisorProtocolErrorKind.TruncatedHeader)]
    [InlineData(SupervisorProtocolErrorKind.BadMagic)]
    [InlineData(SupervisorProtocolErrorKind.UnsupportedVersion)]
    [InlineData(SupervisorProtocolErrorKind.UnknownKind)]
    [InlineData(SupervisorProtocolErrorKind.InvalidPayloadLength)]
    [InlineData(SupervisorProtocolErrorKind.PayloadTooLarge)]
    [InlineData(SupervisorProtocolErrorKind.TruncatedPayload)]
    [InlineData(SupervisorProtocolErrorKind.InvalidPayload)]
    internal async Task MalformedFramesReturnTypedStructuralErrors(
        SupervisorProtocolErrorKind expectedError)
    {
        var bytes = CreateMalformedFrame(expectedError);
        using var stream = new FragmentedReadStream(bytes, maximumReadSize: 2);

        var read = await SupervisorProtocolCodec.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(
            expectedError,
            Assert.IsType<SupervisorProtocolReadRejected>(read).Error.Kind);
    }

    [Fact]
    public async Task LaunchSpecRoundTripsAsTheDefensiveCommonContract()
    {
        var arguments = new List<string> { "fixture.dll", "--probe" };
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DOWNKYI_PROTOCOL_TEST"] = "present",
            ["DOWNKYI_PROTOCOL_REMOVE"] = null
        };
        var expected = new AuthorizeLaunchFrame(new LaunchSpec(
            "dotnet",
            arguments,
            Path.GetTempPath(),
            environment,
            closeStandardInput: true));
        arguments[0] = "mutated.dll";
        environment["DOWNKYI_PROTOCOL_TEST"] = "mutated";
        using var stream = new MemoryStream(Encode(expected), writable: false);

        var read = await SupervisorProtocolCodec.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var actual = Assert.IsType<AuthorizeLaunchFrame>(
            Assert.IsType<SupervisorProtocolFrameRead>(read).Frame).LaunchSpec;
        Assert.Equal("dotnet", actual.FileName);
        Assert.Equal(["fixture.dll", "--probe"], actual.Arguments);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), actual.WorkingDirectory);
        Assert.Equal("present", actual.Environment["DOWNKYI_PROTOCOL_TEST"]);
        Assert.Null(actual.Environment["DOWNKYI_PROTOCOL_REMOVE"]);
        Assert.True(actual.CloseStandardInput);
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)actual.Arguments)[0] = "rewritten.dll");
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, string?>)actual.Environment)["new"] = "value");
    }

    [Fact]
    public void EncodingRejectsAnUnvalidatedOwnershipReadyFact()
    {
        var encoded = SupervisorProtocolCodec.Encode(new OwnershipReadyFrame(
            new ProcessOwnershipMetadata(
                ProcessIdentityAuthority.Unspecified,
                ProcessContainmentKind.Unspecified,
                ProcessContainmentStrength.Unspecified,
                ProcessMembershipAuthority.Unspecified,
                "containment",
                "membership",
                "owner",
                OwnershipEstablished: false)));

        Assert.False(encoded.Succeeded);
        Assert.Equal(SupervisorProtocolErrorKind.InvalidPayload, encoded.Error?.Kind);
    }

    [Fact]
    public async Task MissingLaunchArgumentsReturnATypedPayloadError()
    {
        var bytes = CreateFrame(
            SupervisorProtocolKind.AuthorizeLaunch,
            "{\"fileName\":\"dotnet\",\"workingDirectory\":\"fixture\"}"u8.ToArray());
        using var stream = new MemoryStream(bytes, writable: false);

        var read = await SupervisorProtocolCodec.ReadAsync(
                stream,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(
            SupervisorProtocolErrorKind.InvalidPayload,
            Assert.IsType<SupervisorProtocolReadRejected>(read).Error.Kind);
    }

    private static byte[] Encode(SupervisorProtocolFrame frame)
    {
        var encoded = SupervisorProtocolCodec.Encode(frame);
        Assert.True(encoded.Succeeded, encoded.Error?.Message);
        return Assert.IsType<byte[]>(encoded.Bytes);
    }

    private static byte[] CreateMalformedFrame(SupervisorProtocolErrorKind error)
    {
        if (error == SupervisorProtocolErrorKind.TruncatedHeader)
        {
            return new byte[SupervisorProtocol.HeaderLength - 1];
        }

        var payload = error == SupervisorProtocolErrorKind.InvalidPayload
            ? "{}"u8.ToArray()
            : Array.Empty<byte>();
        var payloadLength = error switch
        {
            SupervisorProtocolErrorKind.InvalidPayloadLength => -1,
            SupervisorProtocolErrorKind.PayloadTooLarge =>
                SupervisorProtocol.MaximumPayloadLength + 1,
            SupervisorProtocolErrorKind.TruncatedPayload => 2,
            _ => payload.Length
        };
        var bytes = new byte[SupervisorProtocol.HeaderLength + payload.Length];
        var header = bytes.AsSpan(0, SupervisorProtocol.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            error == SupervisorProtocolErrorKind.BadMagic ? 0u : SupervisorProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header[sizeof(uint)..],
            error == SupervisorProtocolErrorKind.UnsupportedVersion
                ? checked((ushort)(SupervisorProtocol.Version + 1))
                : SupervisorProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header[(sizeof(uint) + sizeof(ushort))..],
            error == SupervisorProtocolErrorKind.UnknownKind
                ? ushort.MaxValue
                : (ushort)SupervisorProtocolKind.AttachOwnership);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[(sizeof(uint) + sizeof(ushort) + sizeof(ushort))..],
            payloadLength);
        payload.CopyTo(bytes.AsSpan(SupervisorProtocol.HeaderLength));
        return bytes;
    }

    private static byte[] CreateFrame(SupervisorProtocolKind kind, byte[] payload)
    {
        var bytes = new byte[SupervisorProtocol.HeaderLength + payload.Length];
        var header = bytes.AsSpan(0, SupervisorProtocol.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, SupervisorProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[sizeof(uint)..], SupervisorProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header[(sizeof(uint) + sizeof(ushort))..],
            (ushort)kind);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[(sizeof(uint) + sizeof(ushort) + sizeof(ushort))..],
            payload.Length);
        payload.CopyTo(bytes.AsSpan(SupervisorProtocol.HeaderLength));
        return bytes;
    }

    private sealed class FragmentedReadStream : MemoryStream
    {
        private readonly int _maximumReadSize;

        public FragmentedReadStream(byte[] bytes, int maximumReadSize)
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
}
