using System.Security.Cryptography;
using DownKyi.Application.Downloads;
using DownKyi.Core.FFmpeg;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Core.Tests;

public sealed class FfmpegPublicationEvidenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"downkyi-ffmpeg-publication-evidence-{Guid.NewGuid():N}");
    private readonly SettingsStore _settings;

    public FfmpegPublicationEvidenceTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
    }

    [Fact]
    public async Task DashMergeReturnsEvidenceCapturedFromTheTemporaryOutputBeforePublication()
    {
        var audio = CreateFile("audio.m4s", [1, 2, 3]);
        var video = CreateFile("video.m4s", [4, 5, 6]);
        var destination = Path.Combine(_directory, "dash.mp4");
        byte[] media = [8, 6, 7, 5, 3, 0, 9];
        var ownershipProvider = new RecordingOwnershipProvider(destination);
        IFfmpegMediaMuxer processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            new OutputWritingRunner(media));

        var result = await processor.MergeMediaWithEvidenceAsync(
            _settings.Current.Video,
            audio,
            video,
            destination,
            overwriteDestination: false,
            outputArtifactOwnershipProvider: ownershipProvider,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(ownershipProvider.Evidence, result.PublicationEvidence);
        Assert.False(ownershipProvider.DestinationExistedDuringCapture);
        Assert.Equal(media, ownershipProvider.CapturedBytes);
        Assert.Equal(media, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.NotNull(ownershipProvider.TemporaryPath);
        Assert.False(File.Exists(ownershipProvider.TemporaryPath));
    }

    [Fact]
    public async Task DurlConcatReturnsEvidenceCapturedFromTheTemporaryOutputBeforePublication()
    {
        var segment = CreateFile("segment.flv", [1, 2, 3]);
        var destination = Path.Combine(_directory, "durl.mp4");
        byte[] media = [1, 3, 3, 7];
        var ownershipProvider = new RecordingOwnershipProvider(destination);
        IFfmpegMediaMuxer processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            new OutputWritingRunner(media));

        var result = await processor.ConcatDurlVideosWithEvidenceAsync(
            _settings.Current.Video with
            {
                FfmpegHardwareAcceleration = FfmpegHardwareAcceleration.Disabled
            },
            [new FfmpegConcatSegment(1, segment, TimeSpan.FromSeconds(5))],
            destination,
            overwriteDestination: false,
            outputArtifactOwnershipProvider: ownershipProvider,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(ownershipProvider.Evidence, result.PublicationEvidence);
        Assert.False(ownershipProvider.DestinationExistedDuringCapture);
        Assert.Equal(media, ownershipProvider.CapturedBytes);
        Assert.Equal(media, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.NotNull(ownershipProvider.TemporaryPath);
        Assert.False(File.Exists(ownershipProvider.TemporaryPath));
    }

    [Fact]
    public async Task DestinationCollisionNeverReturnsCapturedPublicationEvidence()
    {
        var audio = CreateFile("collision-audio.m4s", [1, 2, 3]);
        var video = CreateFile("collision-video.m4s", [4, 5, 6]);
        var destination = Path.Combine(_directory, "collision.mp4");
        byte[] foreignMedia = [9, 8, 7];
        await File.WriteAllBytesAsync(destination, foreignMedia, TestContext.Current.CancellationToken);
        var ownershipProvider = new RecordingOwnershipProvider(destination);
        IFfmpegMediaMuxer processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            new OutputWritingRunner([1, 2, 3, 4]));

        var result = await processor.MergeMediaWithEvidenceAsync(
            _settings.Current.Video,
            audio,
            video,
            destination,
            overwriteDestination: false,
            outputArtifactOwnershipProvider: ownershipProvider,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.DestinationConflict, result.FailureKind);
        Assert.Null(result.PublicationEvidence);
        Assert.NotNull(ownershipProvider.Evidence);
        Assert.Equal(foreignMedia, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedEvidenceCaptureDoesNotPreventMediaPublication()
    {
        var audio = CreateFile("unsupported-audio.m4s", [1, 2, 3]);
        var video = CreateFile("unsupported-video.m4s", [4, 5, 6]);
        var destination = Path.Combine(_directory, "unsupported.mp4");
        byte[] media = [2, 4, 6, 8];
        IFfmpegMediaMuxer processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            new OutputWritingRunner(media));

        var result = await processor.MergeMediaWithEvidenceAsync(
            _settings.Current.Video,
            audio,
            video,
            destination,
            overwriteDestination: false,
            outputArtifactOwnershipProvider: new UnsupportedOwnershipProvider(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.PublicationEvidence);
        Assert.Equal(media, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvidenceApiDoesNotManufactureEvidenceForLegacyMuxerImplementations()
    {
        IFfmpegMediaMuxer muxer = new LegacyMuxer(new FfmpegOperationResult(
            true,
            Path.Combine(_directory, "legacy.mp4"),
            null,
            TimeSpan.Zero,
            FfmpegOperationFailureKind.None,
            []));

        var result = await muxer.MergeMediaWithEvidenceAsync(
            _settings.Current.Video,
            "audio.m4s",
            "video.m4s",
            Path.Combine(_directory, "legacy.mp4"),
            overwriteDestination: false,
            outputArtifactOwnershipProvider: new UnsupportedOwnershipProvider(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.PublicationEvidence);
    }

    public void Dispose()
    {
        _settings.Dispose();
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreateFile(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class OutputWritingRunner(byte[] output) : IFfmpegProcessRunner
    {
        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Operation == "merge-media" ||
                command.Operation.StartsWith("concat-", StringComparison.Ordinal))
            {
                await File.WriteAllBytesAsync(command.Arguments[^1], output, cancellationToken)
                    .ConfigureAwait(false);
                return new FfmpegProcessResult(
                    true,
                    0,
                    string.Empty,
                    string.Empty,
                    false);
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
                _ => throw new InvalidOperationException($"Unexpected FFmpeg operation: {command.Operation}.")
            };
        }
    }

    private sealed class RecordingOwnershipProvider(string destination) : IOutputArtifactOwnershipProvider
    {
        private sealed record TemporaryClaim : OutputArtifactTemporaryClaim;

        public byte[]? CapturedBytes { get; private set; }

        public bool DestinationExistedDuringCapture { get; private set; }

        public OutputArtifactPublicationEvidence? Evidence { get; private set; }

        public string? TemporaryPath { get; private set; }

        public OutputArtifactTemporaryClaimResult ClaimTemporaryObject(
            SafeFileHandle temporaryHandle)
        {
            Assert.False(temporaryHandle.IsClosed);
            Assert.False(temporaryHandle.IsInvalid);
            return OutputArtifactTemporaryClaimResult.Claimed(new TemporaryClaim());
        }

        public async Task<OutputArtifactEvidenceCaptureResult> CapturePublicationEvidenceAsync(
            string temporaryPath,
            OutputArtifactTemporaryClaim temporaryClaim,
            CancellationToken cancellationToken)
        {
            Assert.IsType<TemporaryClaim>(temporaryClaim);
            cancellationToken.ThrowIfCancellationRequested();
            TemporaryPath = Path.GetFullPath(temporaryPath);
            DestinationExistedDuringCapture = File.Exists(destination);
            var capturedBytes = await File.ReadAllBytesAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            CapturedBytes = capturedBytes;
            Evidence = new OutputArtifactPublicationEvidence(
                capturedBytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(capturedBytes)),
                "test",
                "test-filesystem-identity");
            return OutputArtifactEvidenceCaptureResult.Captured(Evidence);
        }

        public Task<OutputArtifactSafeDeleteResult> DeleteTemporaryIfOwnedAsync(
            string temporaryPath,
            OutputArtifactTemporaryClaim temporaryClaim,
            CancellationToken cancellationToken)
        {
            Assert.IsType<TemporaryClaim>(temporaryClaim);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryPath))
            {
                return Task.FromResult(OutputArtifactSafeDeleteResult.Missing());
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

        public Task<bool> VerifyPublishedObjectIdentityAsync(
            string destinationPath,
            OutputArtifactPublicationEvidence evidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                string.Equals(destination, destinationPath, StringComparison.Ordinal)
                && ReferenceEquals(Evidence, evidence));
        }
    }

    private sealed class UnsupportedOwnershipProvider : IOutputArtifactOwnershipProvider
    {
        public Task<OutputArtifactSafeDeleteResult> DeleteIfOwnedAsync(
            string candidatePath,
            DownloadOutputArtifactProvenance provenance,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(OutputArtifactSafeDeleteResult.Unsupported());
        }
    }

    private sealed class LegacyMuxer(FfmpegOperationResult result) : IFfmpegMediaMuxer
    {
        public Task<FfmpegOperationResult> ConcatDurlVideosAsync(
            VideoApplicationSettings videoSettings,
            IReadOnlyList<FfmpegConcatSegment> segments,
            string outputVideo,
            bool overwriteDestination,
            Action<string>? action = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }

        public Task<FfmpegOperationResult> MergeMediaAsync(
            VideoApplicationSettings videoSettings,
            string? audio,
            string? video,
            string destination,
            bool overwriteDestination,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }
}
