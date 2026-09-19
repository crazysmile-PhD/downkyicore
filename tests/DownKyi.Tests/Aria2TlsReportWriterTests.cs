namespace DownKyi.Tests;

public sealed class Aria2TlsReportWriterTests
{
    [Fact]
    public void BuildSeparatesCompletePassingEvidenceFromPartialEvidence()
    {
        Aria2TlsCaseResult[] cases =
        [
            new("trusted", true, "complete"),
            new("untrusted", true, "rejected")
        ];
        var context = CreateContext();

        var complete = Aria2TlsReportWriter.Build(2, cases, context);
        var partial = Aria2TlsReportWriter.Build(3, cases, context);

        Assert.True(complete.Complete);
        Assert.True(complete.Passed);
        Assert.False(partial.Complete);
        Assert.False(partial.Passed);
        Assert.Same(cases, complete.Cases);
    }

    [Fact]
    public void SerializeThenSanitizeReturnsSafeJsonUnchanged()
    {
        var report = Aria2TlsReportWriter.Build(
            1,
            [new Aria2TlsCaseResult("trusted", true, "complete")],
            CreateContext());

        var reportJson = Aria2TlsReportWriter.Serialize(report);
        var sanitized = Aria2TlsReportWriter.EnsureSanitized(reportJson);

        Assert.Same(reportJson, sanitized);
        Assert.Contains("\"SchemaVersion\": 2", sanitized, StringComparison.Ordinal);
        Assert.Contains("\"Complete\": true", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test-session=fixture")]
    [InlineData("Bearer fixture")]
    [InlineData("Basic Zml4dHVyZQ==")]
    [InlineData("X-Access-Token: fixture")]
    [InlineData("X-API-Key: fixture")]
    [InlineData("sessdata")]
    [InlineData("bili_jct")]
    [InlineData("dedeuserid")]
    [InlineData("http://")]
    [InlineData("https://")]
    [InlineData("C:\\Users\\")]
    [InlineData("/Users/")]
    [InlineData("/home/")]
    public void EnsureSanitizedRejectsSensitiveEvidence(string forbiddenTerm)
    {
        var reportJson = $"{{\"Outcome\":\"{forbiddenTerm}\"}}";

        Assert.Throws<InvalidDataException>(
            () => Aria2TlsReportWriter.EnsureSanitized(reportJson));
    }

    [Fact]
    public async Task WriteCreatesParentDirectoryAndPersistsExactJson()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-aria2-report-writer-{Guid.NewGuid():N}");
        var reportPath = Path.Combine(directory, "nested", "report.json");
        const string reportJson = "{\"SchemaVersion\":2}";
        try
        {
            await Aria2TlsReportWriter.WriteAsync(
                reportPath,
                reportJson,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(
                reportJson,
                await File.ReadAllTextAsync(
                    reportPath,
                    TestContext.Current.CancellationToken).ConfigureAwait(true));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void SerializePreservesSerializerFailure()
    {
        var failure = new InvalidOperationException("serialization failure");
        var report = Aria2TlsReportWriter.Build(
            1,
            [new Aria2TlsCaseResult("trusted", true, "complete")],
            CreateContext());

        var actual = Assert.Throws<InvalidOperationException>(() =>
            Aria2TlsReportWriter.Serialize(report, _ => throw failure));

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task DirectoryCreationFailurePreventsWriteAndRemainsPrimary()
    {
        var failure = new UnauthorizedAccessException("directory creation failure");
        var writeCalled = false;

        var actual = await Record.ExceptionAsync(() => Aria2TlsReportWriter.WriteAsync(
            Path.Combine("report-parent", "report.json"),
            "{}",
            _ => throw failure,
            (_, _, _) =>
            {
                writeCalled = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Same(failure, actual);
        Assert.False(writeCalled);
    }

    [Fact]
    public async Task WriteFailureOccursAfterDirectoryCreationAndRemainsVisible()
    {
        var failure = new IOException("write failure");
        var directoryCreated = false;

        var actual = await Record.ExceptionAsync(() => Aria2TlsReportWriter.WriteAsync(
            Path.Combine("report-parent", "report.json"),
            "{}",
            _ => directoryCreated = true,
            (_, _, _) => Task.FromException(failure),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Same(failure, actual);
        Assert.True(directoryCreated);
    }

    private static Aria2TlsReportContext CreateContext()
    {
        return new Aria2TlsReportContext(
            Runtime: ".NET test",
            OperatingSystem: "test-os",
            Architecture: "X64",
            RuntimeIdentifier: "test-runtime",
            AssetRuntimeIdentifier: "test-asset",
            CommitSha: "test-commit",
            AriaVersion: "1.0-test",
            BinarySha256: new string('A', 64),
            RequiredFeature: Aria2TlsTestRuntime.SecureRedirectFeature,
            TlsBackend: "test-backend",
            CertificateAuthoritySource: "test-root-store");
    }
}
