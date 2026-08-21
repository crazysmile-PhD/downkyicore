using System.Security.Cryptography;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Tests;

public sealed class WindowsOutputArtifactOwnershipProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-output-ownership-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CaptureBeforeRenameAndDeleteAfterRenameUsesWindowsFileIdentity()
    {
        RequireWindows();
        Directory.CreateDirectory(_directory);
        var temporaryPath = CreateFile(".episode.downkyi-tmp.mp4", "owned output");
        var destinationPath = Path.Combine(_directory, "episode.mp4");
        var provider = new WindowsOutputArtifactOwnershipProvider();

        var capture = await CaptureClaimedEvidenceAsync(
            provider,
            temporaryPath,
            TestContext.Current.CancellationToken);

        Assert.True(capture.Succeeded);
        var evidence = Assert.IsType<OutputArtifactPublicationEvidence>(capture.Evidence);
        Assert.Equal("windows-file-id-v1", evidence.IdentityProvider);
        Assert.Equal(new FileInfo(temporaryPath).Length, evidence.ByteLength);
        Assert.Equal(Sha256File(temporaryPath), evidence.Sha256);
        Assert.False(string.IsNullOrWhiteSpace(evidence.FilesystemIdentity));

        File.Move(temporaryPath, destinationPath);
        Assert.True(await provider.VerifyPublishedObjectIdentityAsync(
            destinationPath,
            evidence,
            TestContext.Current.CancellationToken));

        var result = await provider.DeleteIfOwnedAsync(
            destinationPath,
            CreateProvenance(destinationPath, evidence),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutputArtifactSafeDeleteStatus.Deleted, result.Status);
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task PostPublishIdentityVerificationRejectsAReplacementWithoutRehashing()
    {
        RequireWindows();
        Directory.CreateDirectory(_directory);
        var temporaryPath = CreateFile(".episode.downkyi-tmp.mp4", "owned output");
        var destinationPath = Path.Combine(_directory, "episode.mp4");
        var replacementPath = CreateFile(".foreign.downkyi-tmp.mp4", "foreign output");
        var provider = new WindowsOutputArtifactOwnershipProvider();
        var capture = await CaptureClaimedEvidenceAsync(
            provider,
            temporaryPath,
            TestContext.Current.CancellationToken);
        var evidence = Assert.IsType<OutputArtifactPublicationEvidence>(capture.Evidence);

        File.Move(temporaryPath, destinationPath);
        Assert.True(await provider.VerifyPublishedObjectIdentityAsync(
            destinationPath,
            evidence,
            TestContext.Current.CancellationToken));

        File.Move(replacementPath, destinationPath, overwrite: true);

        Assert.False(await provider.VerifyPublishedObjectIdentityAsync(
            destinationPath,
            evidence,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            "foreign output",
            await File.ReadAllTextAsync(
                destinationPath,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteIfOwnedPreservesReplacedOutput()
    {
        RequireWindows();
        Directory.CreateDirectory(_directory);
        var temporaryPath = CreateFile(".episode.downkyi-tmp.mp4", "owned output");
        var destinationPath = Path.Combine(_directory, "episode.mp4");
        var replacementPath = CreateFile(".foreign.downkyi-tmp.mp4", "foreign output");
        var provider = new WindowsOutputArtifactOwnershipProvider();
        var capture = await CaptureClaimedEvidenceAsync(
            provider,
            temporaryPath,
            TestContext.Current.CancellationToken);
        var evidence = Assert.IsType<OutputArtifactPublicationEvidence>(capture.Evidence);

        File.Move(temporaryPath, destinationPath);
        File.Move(replacementPath, destinationPath, overwrite: true);

        var result = await provider.DeleteIfOwnedAsync(
            destinationPath,
            CreateProvenance(destinationPath, evidence),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutputArtifactSafeDeleteStatus.Replaced, result.Status);
        Assert.Equal(
            "foreign output",
            await File.ReadAllTextAsync(
                destinationPath,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteIfOwnedPreservesInPlaceModifiedOutput()
    {
        RequireWindows();
        Directory.CreateDirectory(_directory);
        var temporaryPath = CreateFile(".episode.downkyi-tmp.mp4", "owned output");
        var destinationPath = Path.Combine(_directory, "episode.mp4");
        var provider = new WindowsOutputArtifactOwnershipProvider();
        var capture = await CaptureClaimedEvidenceAsync(
            provider,
            temporaryPath,
            TestContext.Current.CancellationToken);
        var evidence = Assert.IsType<OutputArtifactPublicationEvidence>(capture.Evidence);

        File.Move(temporaryPath, destinationPath);
        await File.WriteAllTextAsync(
            destinationPath,
            "other output",
            TestContext.Current.CancellationToken);

        var result = await provider.DeleteIfOwnedAsync(
            destinationPath,
            CreateProvenance(destinationPath, evidence),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutputArtifactSafeDeleteStatus.Modified, result.Status);
        Assert.Equal(
            "other output",
            await File.ReadAllTextAsync(
                destinationPath,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteIfOwnedReportsMissingProvenOutputAsNoOp()
    {
        RequireWindows();
        Directory.CreateDirectory(_directory);
        var temporaryPath = CreateFile(".episode.downkyi-tmp.mp4", "owned output");
        var destinationPath = Path.Combine(_directory, "episode.mp4");
        var provider = new WindowsOutputArtifactOwnershipProvider();
        var capture = await CaptureClaimedEvidenceAsync(
            provider,
            temporaryPath,
            TestContext.Current.CancellationToken);
        var evidence = Assert.IsType<OutputArtifactPublicationEvidence>(capture.Evidence);

        File.Move(temporaryPath, destinationPath);
        File.Delete(destinationPath);

        var result = await provider.DeleteIfOwnedAsync(
            destinationPath,
            CreateProvenance(destinationPath, evidence),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutputArtifactSafeDeleteStatus.Missing, result.Status);
    }

    [Fact]
    public async Task UnsupportedProviderFailsClosedWithoutOpeningOrDeleting()
    {
        Directory.CreateDirectory(_directory);
        var path = CreateFile("foreign.mp4", "foreign output");
        var fileSystem = new FakeFileSystem
        {
            IsSupported = false
        };
        var provider = new WindowsOutputArtifactOwnershipProvider(fileSystem);
        var evidence = CreateEvidence();

        OutputArtifactTemporaryClaimResult claim;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            claim = provider.ClaimTemporaryObject(stream.SafeFileHandle);
        }

        var deletion = await provider.DeleteIfOwnedAsync(
            path,
            CreateProvenance(path, evidence),
            TestContext.Current.CancellationToken);
        var verified = await provider.VerifyPublishedObjectIdentityAsync(
            path,
            evidence,
            TestContext.Current.CancellationToken);

        Assert.Equal(OutputArtifactTemporaryClaimStatus.Unsupported, claim.Status);
        Assert.Equal(OutputArtifactSafeDeleteStatus.Unsupported, deletion.Status);
        Assert.False(verified);
        Assert.Equal(0, fileSystem.CaptureCalls);
        Assert.Equal(0, fileSystem.DeleteCalls);
        Assert.Equal(0, fileSystem.VerifyCalls);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CanceledDeletionDoesNotOpenTheCandidate()
    {
        var path = Path.Combine(_directory, "episode.mp4");
        var fileSystem = new FakeFileSystem();
        var provider = new WindowsOutputArtifactOwnershipProvider(fileSystem);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.DeleteIfOwnedAsync(
                path,
                CreateProvenance(path, CreateEvidence()),
                cancellation.Token));

        Assert.Equal(0, fileSystem.DeleteCalls);
    }

    [Fact]
    public async Task InvalidOrForeignProviderEvidenceIsUnprovenAndNeverOpened()
    {
        var path = Path.Combine(_directory, "episode.mp4");
        var fileSystem = new FakeFileSystem();
        var provider = new WindowsOutputArtifactOwnershipProvider(fileSystem);
        var evidence = CreateEvidence() with
        {
            IdentityProvider = "foreign-provider"
        };

        var result = await provider.DeleteIfOwnedAsync(
            path,
            CreateProvenance(path, evidence),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutputArtifactSafeDeleteStatus.Unproven, result.Status);
        Assert.Equal(0, fileSystem.DeleteCalls);
    }

    [Fact]
    public async Task NativeReplacementResultPreservesCandidate()
    {
        var path = Path.Combine(_directory, "episode.mp4");
        var fileSystem = new FakeFileSystem
        {
            DeleteResult = OutputArtifactNativeSafeDeleteResult.Replaced()
        };
        var provider = new WindowsOutputArtifactOwnershipProvider(fileSystem);

        var result = await provider.DeleteIfOwnedAsync(
            path,
            CreateProvenance(path, CreateEvidence()),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutputArtifactSafeDeleteStatus.Replaced, result.Status);
        Assert.Equal(1, fileSystem.DeleteCalls);
    }

    [Fact]
    public async Task NativeModifiedResultPreservesCandidate()
    {
        var path = Path.Combine(_directory, "episode.mp4");
        var fileSystem = new FakeFileSystem
        {
            DeleteResult = OutputArtifactNativeSafeDeleteResult.Modified()
        };
        var provider = new WindowsOutputArtifactOwnershipProvider(fileSystem);

        var result = await provider.DeleteIfOwnedAsync(
            path,
            CreateProvenance(path, CreateEvidence()),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutputArtifactSafeDeleteStatus.Modified, result.Status);
        Assert.Equal(1, fileSystem.DeleteCalls);
    }

    [Fact]
    public async Task ValidatedDeletionDelegatesToTheHandleBoundOperationWhenThePathIsSwapped()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "episode.mp4");
        await File.WriteAllTextAsync(
            path,
            "validated object",
            TestContext.Current.CancellationToken);
        var foreignPath = CreateFile("foreign.mp4", "foreign replacement");
        var replacementStillExists = false;
        var originalDeleted = false;
        var fileSystem = new FakeFileSystem
        {
            DeleteResult = OutputArtifactNativeSafeDeleteResult.Deleted(),
            OnDeleteIfEvidenceMatches = () =>
            {
                File.Move(foreignPath, path, overwrite: true);
                replacementStillExists = true;
                originalDeleted = true;
            }
        };
        var provider = new WindowsOutputArtifactOwnershipProvider(fileSystem);

        var result = await provider.DeleteIfOwnedAsync(
            path,
            CreateProvenance(path, CreateEvidence()),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutputArtifactSafeDeleteStatus.Deleted, result.Status);
        Assert.True(originalDeleted);
        Assert.True(replacementStillExists);
        Assert.Equal(
            "foreign replacement",
            await File.ReadAllTextAsync(
                path,
                TestContext.Current.CancellationToken));
        Assert.Equal(1, fileSystem.DeleteCalls);
    }

    private static async Task<OutputArtifactEvidenceCaptureResult> CaptureClaimedEvidenceAsync(
        WindowsOutputArtifactOwnershipProvider provider,
        string path,
        CancellationToken cancellationToken)
    {
        OutputArtifactTemporaryClaim claim;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var claimed = provider.ClaimTemporaryObject(stream.SafeFileHandle);
            Assert.True(claimed.Succeeded);
            claim = Assert.IsAssignableFrom<OutputArtifactTemporaryClaim>(claimed.Claim);
        }

        return await provider
            .CapturePublicationEvidenceAsync(path, claim, cancellationToken)
            .ConfigureAwait(true);
    }

    private static DownloadOutputArtifactProvenance CreateProvenance(
        string path,
        OutputArtifactPublicationEvidence evidence)
    {
        return new DownloadOutputArtifactProvenance(
            new DownloadTaskId("safe-delete-test"),
            "media",
            "media",
            path,
            evidence,
            DateTimeOffset.UtcNow);
    }

    private static OutputArtifactPublicationEvidence CreateEvidence()
    {
        return new OutputArtifactPublicationEvidence(
            ByteLength: 12,
            Sha256: new string('a', 64),
            IdentityProvider: WindowsOutputArtifactOwnershipProvider.IdentityProviderName,
            FilesystemIdentity: "owned");
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256File(string path)
    {
        return Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows handle-bound provider is only available on Windows.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeFileSystem : IOutputArtifactNativeFileSystem
    {
        public bool IsSupported { get; init; } = true;

        public OutputArtifactNativeCaptureResult CaptureResult { get; init; } =
            OutputArtifactNativeCaptureResult.Failed();

        public OutputArtifactNativeIdentityCaptureResult IdentityCaptureResult { get; init; } =
            OutputArtifactNativeIdentityCaptureResult.Failed();

        public OutputArtifactNativeSafeDeleteResult DeleteResult { get; init; } =
            OutputArtifactNativeSafeDeleteResult.Failed();

        public bool VerifyResult { get; init; }

        public Action? OnDeleteIfEvidenceMatches { get; init; }

        public int CaptureCalls { get; private set; }

        public int IdentityCaptureCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public int VerifyCalls { get; private set; }

        public OutputArtifactNativeIdentityCaptureResult CaptureIdentity(
            SafeFileHandle handle)
        {
            IdentityCaptureCalls++;
            return IdentityCaptureResult;
        }

        public Task<OutputArtifactNativeCaptureResult> CaptureEvidenceAsync(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCalls++;
            return Task.FromResult(CaptureResult);
        }

        public Task<OutputArtifactNativeSafeDeleteResult> DeleteIfEvidenceMatchesAsync(
            string path,
            OutputArtifactNativeEvidence expectedEvidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCalls++;
            OnDeleteIfEvidenceMatches?.Invoke();
            return Task.FromResult(DeleteResult);
        }

        public Task<OutputArtifactNativeSafeDeleteResult> DeleteIfIdentityMatchesAsync(
            string path,
            string expectedFilesystemIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCalls++;
            OnDeleteIfEvidenceMatches?.Invoke();
            return Task.FromResult(DeleteResult);
        }

        public Task<bool> VerifyIdentityAndLengthAsync(
            string path,
            OutputArtifactNativeEvidence expectedEvidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            return Task.FromResult(VerifyResult);
        }
    }
}
