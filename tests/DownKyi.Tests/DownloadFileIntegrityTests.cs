using DownKyi.Core.Aria2cNet;
using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class DownloadFileIntegrityTests : IDisposable
{
    private readonly string _directory;

    public DownloadFileIntegrityTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"downkyi-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void IsUsableRejectsEmptyFile()
    {
        var file = CreateFile("empty.mp4", Array.Empty<byte>());

        Assert.False(DownloadFileIntegrity.IsUsable(file));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>blocked</body></html>")]
    [InlineData("<html><body>blocked</body></html>")]
    [InlineData("{\"code\":-403,\"message\":\"forbidden\"}")]
    [InlineData("{\"error\":\"forbidden\"}")]
    public void IsUsableRejectsErrorPayloads(string payload)
    {
        var file = CreateFile("error.mp4", payload);

        Assert.False(DownloadFileIntegrity.IsUsable(file));
    }

    [Theory]
    [InlineData(".aria2")]
    [InlineData(".download")]
    public void IsUsableRejectsUnfinishedSidecars(string sidecarExtension)
    {
        var file = CreateFile("video.mp4", new byte[] { 0, 1, 2, 3 });
        File.WriteAllText($"{file}{sidecarExtension}", "unfinished");

        Assert.False(DownloadFileIntegrity.IsUsable(file));
    }

    [Fact]
    public void IsUsableRejectsIncompleteExpectedLength()
    {
        var file = CreateFile("short.mp4", new byte[] { 0, 1, 2, 3 });

        Assert.False(DownloadFileIntegrity.IsUsable(file, expectedBytes: 8, receivedBytes: 4));
    }

    [Fact]
    public void IsUsableRejectsLengthBeyondAuthoritativeExpectedLength()
    {
        var file = CreateFile("long.mp4", new byte[] { 0, 1, 2, 3, 4 });

        Assert.False(DownloadFileIntegrity.IsUsable(file, expectedBytes: 4, receivedBytes: 4));
    }

    [Fact]
    public void IsUsableAcceptsNonEmptyMediaLikeFile()
    {
        var file = CreateFile("video.mp4", new byte[] { 0, 1, 2, 3 });

        Assert.True(DownloadFileIntegrity.IsUsable(file, expectedBytes: 4, receivedBytes: 4));
    }

    [Fact]
    public void IsUsableDoesNotInventExactLengthWhenContractDoesNotProvideOne()
    {
        var file = CreateFile("unknown-length.mp4", new byte[] { 0, 1, 2, 3, 4 });

        Assert.True(DownloadFileIntegrity.IsUsable(file));
    }

    [Fact]
    public void AriaCompletionRequiresNativeEvidenceAndMatchingFileLength()
    {
        var file = CreateFile("aria.mp4", new byte[] { 0, 1, 2, 3 });
        var completeEvidence = new AriaTransferCompletionEvidence(4, 4, "80", 1);

        var result = Aria2TransferBackend.ValidateCompletedTransfer(
            file,
            exactExpectedBytes: 4,
            completeEvidence);

        Assert.Equal(DownloadTransferOutcome.Succeeded, result.Outcome);

        var wrongLength = Aria2TransferBackend.ValidateCompletedTransfer(
            file,
            exactExpectedBytes: 5,
            completeEvidence);
        Assert.Equal(DownloadTransferOutcome.Failed, wrongLength.Outcome);
        Assert.Equal(DownloadTransferFailureKind.InvalidMedia, wrongLength.FailureKind);

        var incompletePieces = Aria2TransferBackend.ValidateCompletedTransfer(
            file,
            exactExpectedBytes: 4,
            completeEvidence with { Bitfield = "00" });
        Assert.Equal(DownloadTransferOutcome.Failed, incompletePieces.Outcome);
        Assert.Equal(DownloadTransferFailureKind.InvalidMedia, incompletePieces.FailureKind);
    }

    private string CreateFile(string name, string content)
    {
        var file = Path.Combine(_directory, name);
        File.WriteAllText(file, content);
        return file;
    }

    private string CreateFile(string name, byte[] content)
    {
        var file = Path.Combine(_directory, name);
        File.WriteAllBytes(file, content);
        return file;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
