using System.Reflection;
using System.Security.Cryptography;
using DownKyi.Application.Downloads;
using DownKyi.Core.FFmpeg;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Tests;

public sealed class OutputArtifactOwnershipAdversarialTests : IDisposable
{
    private const string MergePath = "merge";
    private const string ConcatPath = "concat";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-output-ownership-adversarial",
        Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _settings;

    public OutputArtifactOwnershipAdversarialTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
    }

    [Fact]
    public async Task AtomicPublisherDoesNotGrantProvenanceToSubstitutedProducerObject()
    {
        RequireWindows();
        var destination = Path.Combine(_directory, "atomic-substitution.mp4");
        var publisher = new AtomicOutputPublisher(new WindowsOutputArtifactOwnershipProvider());

        var result = await publisher.PublishAsync(
            destination,
            async (temporaryPath, cancellationToken) =>
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    "producer object",
                    cancellationToken).ConfigureAwait(true);
                File.Move(temporaryPath, $"{temporaryPath}.producer");
                await File.WriteAllTextAsync(
                    $"{temporaryPath}.foreign",
                    "foreign substitution",
                    cancellationToken).ConfigureAwait(true);
                File.Move($"{temporaryPath}.foreign", temporaryPath);
            },
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded);
        Assert.Null(result.PublicationEvidence);
        Assert.Equal(
            "foreign substitution",
            await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken)
                .ConfigureAwait(true));
    }

    [Theory]
    [InlineData(MergePath)]
    [InlineData(ConcatPath)]
    public async Task FfmpegDoesNotGrantProvenanceToSubstitutedProducerObject(string publicationPath)
    {
        RequireWindows();
        var destination = Path.Combine(_directory, $"ffmpeg-{publicationPath}-substitution.mp4");
        byte[] foreignOutput = [9, 8, 7, 6];
        var runner = new AdversarialFfmpegRunner(async (temporaryPath, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(temporaryPath, [1, 2, 3, 4], cancellationToken)
                .ConfigureAwait(true);
            File.Move(temporaryPath, $"{temporaryPath}.producer");
            await File.WriteAllBytesAsync(
                $"{temporaryPath}.foreign",
                foreignOutput,
                cancellationToken).ConfigureAwait(true);
            File.Move($"{temporaryPath}.foreign", temporaryPath);
        });

        var result = await RunFfmpegPublicationAsync(
            publicationPath,
            destination,
            runner,
            new WindowsOutputArtifactOwnershipProvider(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded);
        Assert.Null(result.PublicationEvidence);
        Assert.Equal(
            foreignOutput,
            await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken)
                .ConfigureAwait(true));
    }

    [Fact]
    public async Task AtomicPublisherCancellationAfterCapturePreventsPublicationMove()
    {
        var destination = Path.Combine(_directory, "atomic-canceled.mp4");
        using var cancellation = new CancellationTokenSource();
        var publisher = new AtomicOutputPublisher(
            new AdversarialOwnershipProvider(onCapture: _ => cancellation.Cancel()));

        var exception = await Record.ExceptionAsync(() => publisher.PublishAsync(
            destination,
            (temporaryPath, cancellationToken) => File.WriteAllTextAsync(
                temporaryPath,
                "completed producer output",
                cancellationToken),
            cancellation.Token)).ConfigureAwait(true);

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData(MergePath)]
    [InlineData(ConcatPath)]
    public async Task FfmpegCancellationAfterCapturePreventsPublicationMove(string publicationPath)
    {
        var destination = Path.Combine(_directory, $"ffmpeg-{publicationPath}-canceled.mp4");
        using var cancellation = new CancellationTokenSource();
        var provider = new AdversarialOwnershipProvider(onCapture: _ => cancellation.Cancel());
        var runner = new AdversarialFfmpegRunner((temporaryPath, cancellationToken) =>
            File.WriteAllBytesAsync(temporaryPath, [1, 2, 3, 4], cancellationToken));

        var exception = await Record.ExceptionAsync(() => RunFfmpegPublicationAsync(
            publicationPath,
            destination,
            runner,
            provider,
            cancellation.Token)).ConfigureAwait(true);

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task AtomicPublisherTempCleanupPreservesReplacementObject()
    {
        var destination = Path.Combine(_directory, "atomic-cleanup.mp4");
        var provider = new AdversarialOwnershipProvider(onVerify: temporaryPath =>
            File.WriteAllText(temporaryPath, "foreign temp replacement"));
        var publisher = new AtomicOutputPublisher(provider);

        var result = await publisher.PublishAsync(
            destination,
            (temporaryPath, cancellationToken) => File.WriteAllTextAsync(
                temporaryPath,
                "producer output",
                cancellationToken),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded);
        var temporaryPath = Assert.IsType<string>(provider.TemporaryPath);
        Assert.Equal(
            "foreign temp replacement",
            await File.ReadAllTextAsync(temporaryPath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true));
    }

    [Theory]
    [InlineData(MergePath)]
    [InlineData(ConcatPath)]
    public async Task FfmpegTempCleanupPreservesReplacementObject(string publicationPath)
    {
        var destination = Path.Combine(_directory, $"ffmpeg-{publicationPath}-cleanup.mp4");
        var provider = new AdversarialOwnershipProvider(onVerify: temporaryPath =>
            File.WriteAllText(temporaryPath, "foreign temp replacement"));
        var runner = new AdversarialFfmpegRunner((temporaryPath, cancellationToken) =>
            File.WriteAllBytesAsync(temporaryPath, [1, 2, 3, 4], cancellationToken));

        var result = await RunFfmpegPublicationAsync(
            publicationPath,
            destination,
            runner,
            provider,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded);
        var temporaryPath = Assert.IsType<string>(provider.TemporaryPath);
        Assert.Equal(
            "foreign temp replacement",
            await File.ReadAllTextAsync(temporaryPath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true));
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(ulong.MaxValue, ulong.MaxValue)]
    public void FileId128SentinelsFailAtAuthoritativeConversionBoundary(
        ulong fileIdHigh,
        ulong fileIdLow)
    {
        var conversion = typeof(WindowsOutputArtifactNativeFileSystem).GetMethod(
            "FormatFileIdForEvidence",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(conversion);

        var exception = Assert.Throws<TargetInvocationException>(() => conversion.Invoke(
            null,
            [0x123456789abcdef0UL, fileIdHigh, fileIdLow]));
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task NativeDeleteKeepsValidatedHandleAcrossDestructiveBoundary()
    {
        RequireWindows();
        var candidate = CreateFile("native-delete.mp4", "validated object");
        var replacement = CreateFile("native-replacement.mp4", "foreign replacement");
        var displaced = Path.Combine(_directory, "native-displaced.mp4");
        var constructor = typeof(WindowsOutputArtifactNativeFileSystem).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(Action<SafeFileHandle>)],
            modifiers: null);
        Assert.NotNull(constructor);

        var replacementWasPerformed = false;
        Action<SafeFileHandle> beforeDelete = handle =>
        {
            Assert.False(handle.IsClosed);
            Assert.False(handle.IsInvalid);
            File.Move(candidate, displaced);
            File.Move(replacement, candidate);
            replacementWasPerformed = true;
        };
        var fileSystem = Assert.IsAssignableFrom<IOutputArtifactNativeFileSystem>(
            constructor.Invoke([beforeDelete]));
        var capture = await fileSystem.CaptureEvidenceAsync(
            candidate,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var evidence = Assert.IsType<OutputArtifactNativeEvidence>(capture.Evidence);

        var result = await fileSystem.DeleteIfEvidenceMatchesAsync(
            candidate,
            evidence,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(replacementWasPerformed, $"Native delete returned {result.Status} before the boundary callback.");
        Assert.True(result.Status is OutputArtifactNativeSafeDeleteStatus.Deleted
            or OutputArtifactNativeSafeDeleteStatus.Failed);
        Assert.Equal(
            "foreign replacement",
            await File.ReadAllTextAsync(candidate, TestContext.Current.CancellationToken)
                .ConfigureAwait(true));
        Assert.False(File.Exists(replacement));
    }

    private async Task<FfmpegOperationResult> RunFfmpegPublicationAsync(
        string publicationPath,
        string destination,
        IFfmpegProcessRunner runner,
        IOutputArtifactOwnershipProvider provider,
        CancellationToken cancellationToken)
    {
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);
        if (publicationPath == MergePath)
        {
            var audio = CreateFile($"{Path.GetFileNameWithoutExtension(destination)}-audio.m4s", "audio");
            var video = CreateFile($"{Path.GetFileNameWithoutExtension(destination)}-video.m4s", "video");
            return await processor.MergeMediaWithEvidenceAsync(
                _settings.Current.Video,
                audio,
                video,
                destination,
                overwriteDestination: false,
                outputArtifactOwnershipProvider: provider,
                cancellationToken: cancellationToken).ConfigureAwait(true);
        }

        var segment = CreateFile(
            $"{Path.GetFileNameWithoutExtension(destination)}-segment.flv",
            "segment");
        return await processor.ConcatDurlVideosWithEvidenceAsync(
            _settings.Current.Video with
            {
                FfmpegHardwareAcceleration = FfmpegHardwareAcceleration.Disabled
            },
            [new FfmpegConcatSegment(1, segment, TimeSpan.FromSeconds(5))],
            destination,
            overwriteDestination: false,
            outputArtifactOwnershipProvider: provider,
            cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows object identity is required for this regression.");
        }
    }

    public void Dispose()
    {
        _settings.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class AdversarialOwnershipProvider(
        Action<string>? onCapture = null,
        Action<string>? onVerify = null) : IOutputArtifactOwnershipProvider
    {
        private static readonly OutputArtifactPublicationEvidence Evidence = new(
            ByteLength: 4,
            Sha256: Convert.ToHexStringLower(SHA256.HashData([1, 2, 3, 4])),
            IdentityProvider: "adversarial-test",
            FilesystemIdentity: "adversarial-object");

        private bool _temporaryWasReplaced;

        public string? TemporaryPath { get; private set; }

        public OutputArtifactTemporaryClaimResult ClaimTemporaryObject(
            SafeFileHandle temporaryHandle)
        {
            return OutputArtifactTemporaryClaimResult.Claimed(new TemporaryClaim());
        }

        public Task<OutputArtifactEvidenceCaptureResult> CapturePublicationEvidenceAsync(
            string temporaryPath,
            OutputArtifactTemporaryClaim temporaryClaim,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemporaryPath = Path.GetFullPath(temporaryPath);
            onCapture?.Invoke(TemporaryPath);
            return Task.FromResult(OutputArtifactEvidenceCaptureResult.Captured(Evidence));
        }

        public Task<bool> VerifyPublishedObjectIdentityAsync(
            string destinationPath,
            OutputArtifactPublicationEvidence evidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onVerify is not null)
            {
                onVerify(Assert.IsType<string>(TemporaryPath));
                _temporaryWasReplaced = true;
            }

            return Task.FromResult(true);
        }

        public Task<OutputArtifactSafeDeleteResult> DeleteTemporaryIfOwnedAsync(
            string temporaryPath,
            OutputArtifactTemporaryClaim temporaryClaim,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryPath))
            {
                return Task.FromResult(OutputArtifactSafeDeleteResult.Missing());
            }

            if (_temporaryWasReplaced)
            {
                return Task.FromResult(OutputArtifactSafeDeleteResult.Replaced());
            }

            File.Delete(temporaryPath);
            return Task.FromResult(OutputArtifactSafeDeleteResult.DeletedResult());
        }

        public Task<OutputArtifactSafeDeleteResult> DeleteIfOwnedAsync(
            string candidatePath,
            DownloadOutputArtifactProvenance provenance,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(OutputArtifactSafeDeleteResult.Unsupported());
        }

        private sealed record TemporaryClaim : OutputArtifactTemporaryClaim;
    }

    private sealed class AdversarialFfmpegRunner(
        Func<string, CancellationToken, Task> writeOutput) : IFfmpegProcessRunner
    {
        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Operation == "merge-media"
                || command.Operation.StartsWith("concat-", StringComparison.Ordinal))
            {
                await writeOutput(command.Arguments[^1], cancellationToken).ConfigureAwait(true);
                return Success();
            }

            return command.Operation switch
            {
                "probe-media" => new FfmpegProcessResult(
                    true,
                    0,
                    "{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"5\"}}",
                    string.Empty,
                    false),
                "seek-decode" => new FfmpegProcessResult(
                    true,
                    0,
                    "frame=1",
                    string.Empty,
                    false),
                _ => throw new InvalidOperationException(
                    $"Unexpected FFmpeg operation: {command.Operation}.")
            };
        }

        private static FfmpegProcessResult Success() => new(
            true,
            0,
            string.Empty,
            string.Empty,
            false);
    }
}
