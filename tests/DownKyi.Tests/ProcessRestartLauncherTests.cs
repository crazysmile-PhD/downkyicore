using DownKyi.Platform;

namespace DownKyi.Tests;

public sealed class ProcessRestartLauncherTests
{
    [Theory]
    [InlineData("--restart-after-pid", "1", 1)]
    [InlineData("--restart-after-pid", "2147483647", int.MaxValue)]
    public void RestartHelperArgumentsAcceptOnlyPositiveProcessIds(
        string option,
        string value,
        int expectedProcessId)
    {
        var parsed = ProcessRestartLauncher.TryParseParentProcessId(
            [option, value],
            out var processId);

        Assert.True(parsed);
        Assert.Equal(expectedProcessId, processId);
    }

    [Theory]
    [InlineData()]
    [InlineData("--restart-after-pid")]
    [InlineData("--restart-after-pid", "0")]
    [InlineData("--restart-after-pid", "-1")]
    [InlineData("--restart-after-pid", "not-a-process")]
    [InlineData("--unrelated", "42")]
    [InlineData("--restart-after-pid", "42", "extra")]
    public void RestartHelperArgumentsRejectMalformedInput(params string[] arguments)
    {
        Assert.False(ProcessRestartLauncher.TryParseParentProcessId(arguments, out _));
    }

    [Fact]
    public void RestartStartInfoUsesArgumentListWithoutShellParsing()
    {
        var startInfo = ProcessRestartLauncher.CreateStartInfo(42);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            ProcessRestartLauncher.WaitForParentArgument,
            startInfo.ArgumentList[^2]);
        Assert.Equal("42", startInfo.ArgumentList[^1]);
        Assert.DoesNotContain(ProcessRestartLauncher.WaitForParentArgument, startInfo.Arguments, StringComparison.Ordinal);
    }
}
