using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class IpcEndpointNameTests
{
    [Fact]
    public void PhysicalIdentifierUsesOneBoundedAsciiPolicy()
    {
        var endpoint = IpcEndpointName.Create("OwnedProcessLease.Control");

        Assert.Equal(
            IpcEndpointName.GeneratedPhysicalIdentifierLength,
            endpoint.PhysicalIdentifier.Length);
        Assert.Equal(
            IpcEndpointName.GeneratedPhysicalIdentifierLength,
            IpcEndpointName.PhysicalIdentifierPrefix.Length +
                IpcEndpointName.TokenCharacterCount);
        Assert.InRange(
            endpoint.PhysicalIdentifier.Length,
            IpcEndpointName.PhysicalIdentifierPrefix.Length + 1,
            IpcEndpointName.MaximumPhysicalIdentifierLength);
        Assert.StartsWith(
            IpcEndpointName.PhysicalIdentifierPrefix,
            endpoint.PhysicalIdentifier,
            StringComparison.Ordinal);
        Assert.All(endpoint.PhysicalIdentifier, character => Assert.InRange((int)character, 0, 127));
        Assert.Matches("^[a-z0-9-]+$", endpoint.PhysicalIdentifier);
    }

    [Fact]
    public void ParallelCreationKeepsCaseInsensitivePhysicalIdentifiersUnique()
    {
        const int identifierCount = 16_384;
        var identifiers = new ConcurrentDictionary<string, byte>(
            StringComparer.OrdinalIgnoreCase);
        var duplicates = new ConcurrentQueue<string>();

        Parallel.For(0, identifierCount, index =>
        {
            var endpoint = IpcEndpointName.Create($"Parallel test logical label {index}");
            if (!identifiers.TryAdd(endpoint.PhysicalIdentifier, 0))
            {
                duplicates.Enqueue(endpoint.PhysicalIdentifier);
            }
        });

        Assert.Empty(duplicates);
        Assert.Equal(identifierCount, identifiers.Count);
    }

    [Fact]
    public void LogicalLabelLengthCannotChangePhysicalIdentifierLength()
    {
        var shortEndpoint = IpcEndpointName.Create("short");
        var longLogicalLabel = new string('L', 16_384);
        var longEndpoint = IpcEndpointName.Create(longLogicalLabel);

        Assert.Equal("short", shortEndpoint.LogicalLabel);
        Assert.Equal(longLogicalLabel, longEndpoint.LogicalLabel);
        Assert.Equal(
            shortEndpoint.PhysicalIdentifier.Length,
            longEndpoint.PhysicalIdentifier.Length);
        Assert.DoesNotContain("LLL", longEndpoint.PhysicalIdentifier, StringComparison.Ordinal);
        Assert.Contains(longLogicalLabel, longEndpoint.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            longEndpoint.PhysicalIdentifier,
            longEndpoint.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MacOsDotNetNamedPipePathBudgetRetainsTerminatorSpace()
    {
        var endpoint = IpcEndpointName.Create(
            "OrdinaryLeaseRegression_LateWatcher_With_An_Intentionally_Long_Logical_Label");
        var maximumReservedPrefix = new string(
            'p',
            IpcEndpointName.MaximumMacOsDotNetPipePrefixBytes);
        var encodedPathBytes = Encoding.ASCII.GetByteCount(
            maximumReservedPrefix + endpoint.PhysicalIdentifier);

        Assert.True(
            encodedPathBytes + IpcEndpointName.UnixDomainSocketTerminatorBytes <=
                IpcEndpointName.MacOsUnixDomainSocketPathLimitBytes,
            $"{endpoint.LogicalLabel}: physical path budget was {encodedPathBytes} bytes for " +
            $"'{endpoint.PhysicalIdentifier}'.");

        using var pipe = new NamedPipeServerStream(
            endpoint.PhysicalIdentifier,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }
}
