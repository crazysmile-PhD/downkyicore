using System.IO.Pipes;
using DownKyi.ProcessSupervision;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class OwnedProcessLeasePipeNameTests
{
    [Fact]
    public void PipeNamesAreShortPortableAndUniquePerLease()
    {
        var first = OwnedProcessLease.CreatePipeNames(
            Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var second = OwnedProcessLease.CreatePipeNames(
            Guid.Parse("00000000-0000-0000-0000-000000000002"));

        AssertPortable(first.CommandPipeName);
        AssertPortable(first.StatusPipeName);
        Assert.NotEqual(first.CommandPipeName, first.StatusPipeName);
        Assert.NotEqual(first.CommandPipeName, second.CommandPipeName);
        Assert.NotEqual(first.StatusPipeName, second.StatusPipeName);
    }

    [Fact]
    public void ShortPortableNamesCanCreateBothPipeEndpoints()
    {
        var names = OwnedProcessLease.CreatePipeNames(Guid.NewGuid());

        using var commands = new NamedPipeServerStream(
            names.CommandPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var status = new NamedPipeServerStream(
            names.StatusPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Assert.False(commands.IsConnected);
        Assert.False(status.IsConnected);
    }

    private static void AssertPortable(string pipeName)
    {
        Assert.Equal(23, pipeName.Length);
        Assert.All(
            pipeName,
            character => Assert.True(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }
}
