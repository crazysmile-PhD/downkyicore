namespace DownKyi.Architecture.Tests;

public sealed class ProcessContainmentArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void RouterSelectionConsumesOnlyImmutableFacts()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "DownKyi.ProcessSupervision",
            "PlatformProcessContainmentRouter.cs"));
        var selectionStart = source.IndexOf(
            "public static IProcessContainmentBackend Select(",
            StringComparison.Ordinal);
        var selectionEnd = source.IndexOf(
            "public static IProcessContainmentBackend SelectEstablished(",
            selectionStart,
            StringComparison.Ordinal);
        Assert.True(selectionStart >= 0 && selectionEnd > selectionStart);
        var selection = source[selectionStart..selectionEnd];

        foreach (var forbidden in new[]
                 {
                     "OperatingSystem.",
                     "TransitionBudget",
                     "OwnedProcessFailure",
                     "Task.Delay",
                     "Thread.Sleep",
                     "WaitForExit",
                     ".Prepare(",
                     ".Terminate("
                 })
        {
            Assert.DoesNotContain(forbidden, selection, StringComparison.Ordinal);
        }

        Assert.Contains(
            "public static PlatformContainmentFacts CapturePlatformFacts()",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForExit", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Terminate(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformBackendsRemainSeparateFiles()
    {
        foreach (var fileName in new[]
                 {
                     "WindowsJobContainmentLease.cs",
                     "LinuxCgroupContainmentLease.cs",
                     "LinuxProcessGroupContainmentLease.cs",
                     "MacProcessGroupContainmentLease.cs",
                     "PosixProcessGroupNative.cs"
                 })
        {
            Assert.True(File.Exists(Path.Combine(
                RepositoryRoot,
                "tools",
                "DownKyi.ProcessSupervision",
                fileName)), fileName);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }
}
