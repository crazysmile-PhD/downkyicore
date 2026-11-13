using DownKyi.Domain.Results;
using DownKyi.Services.Download;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadFailurePresentationTests
{
    private static readonly string[] UnknownFailureCodes =
    [
        "download.artifact.subtitle.future.io",
        "download.transfer.tls.future"
    ];

    [AvaloniaFact]
    public Task MissingSelectedSubtitleShowsSpecificFailureInsteadOfGenericDownloadFailure()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var error = OperationError.Unexpected(
                "download.artifact.subtitle.missing-tracks",
                "A selected subtitle was not returned.");

            var failure = DownloadActivityPresenter.CreateFailure(error);

            Assert.Equal("download.artifact.subtitle.missing-tracks", failure.Code);
            Assert.Equal("字幕下载失败", failure.Message);
            Assert.True(failure.IsTransient);
        });
    }

    [AvaloniaFact]
    public Task TransferFailureKeepsItsCodeAndExplainsTheNextStepWithoutExposingTheRawError()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var error = OperationError.Unexpected(
                "download.transfer.http-403",
                "https://example.invalid/video?token=private-value");

            var failure = DownloadActivityPresenter.CreateFailure(error);
            var details = DownloadActivityPresenter.FormatFailureDetails(failure);

            Assert.Equal("download.transfer.http-403", failure.Code);
            Assert.DoesNotContain("private-value", failure.Message, StringComparison.Ordinal);
            Assert.Contains("地址已失效", details, StringComparison.Ordinal);
            Assert.Contains("已经尝试切换并刷新地址", details, StringComparison.Ordinal);
            Assert.Contains("download.transfer.http-403", details, StringComparison.Ordinal);
            Assert.DoesNotContain("private-value", details, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public Task UnauthorizedTransferExplainsAccessRecovery()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var details = DownloadActivityPresenter.FormatFailureDetails(
                new DownKyi.Domain.Downloads.DownloadFailure(
                    "download.transfer.http-401",
                    "下载失败",
                    true));

            Assert.Contains("服务器拒绝", details, StringComparison.Ordinal);
            Assert.Contains("检查登录状态", details, StringComparison.Ordinal);
            Assert.Contains("download.transfer.http-401", details, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public Task ArtifactAccessFailureDoesNotSuggestChangingMediaQuality()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var details = DownloadActivityPresenter.FormatFailureDetails(
                new DownKyi.Domain.Downloads.DownloadFailure(
                    "download.artifact.subtitle.http-403",
                    "字幕下载失败",
                    true));

            Assert.Contains("附加文件请求", details, StringComparison.Ordinal);
            Assert.Contains("检查登录状态", details, StringComparison.Ordinal);
            Assert.DoesNotContain("画质", details, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public Task AnonymousCoverAccessFailureDoesNotSuggestSigningIn()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var details = DownloadActivityPresenter.FormatFailureDetails(
                new DownKyi.Domain.Downloads.DownloadFailure(
                    "download.artifact.cover.http-403",
                    "封面下载失败",
                    true));

            Assert.Contains("封面请求", details, StringComparison.Ordinal);
            Assert.DoesNotContain("登录", details, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public Task TlsFailureAllowsManualRetryAfterSettingsAreCorrected()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var expectations = new[]
            {
                ("download.transfer.tls.untrusted", "下载失败：服务器证书不受信任"),
                ("download.transfer.tls.expired", "下载失败：服务器证书已过期"),
                ("download.transfer.tls.not-yet-valid", "下载失败：服务器证书尚未生效"),
                ("download.transfer.tls.hostname", "下载失败：服务器证书与主机名不匹配"),
                ("download.transfer.tls.chain", "下载失败：服务器证书链无效"),
                ("download.transfer.tls.handshake", "下载失败：TLS 安全连接验证失败")
            };
            foreach (var (code, expectedMessage) in expectations)
            {
                var failure = DownloadActivityPresenter.CreateFailure(OperationError.Unexpected(
                    code,
                    "Sensitive certificate details."));
                var details = DownloadActivityPresenter.FormatFailureDetails(failure);

                Assert.Equal(code, failure.Code);
                Assert.Equal(expectedMessage, failure.Message);
                Assert.True(failure.IsTransient);
                Assert.Contains("证书设置", details, StringComparison.Ordinal);
                Assert.Contains("再重试", details, StringComparison.Ordinal);
            }
        });
    }

    [AvaloniaFact]
    public Task MissingPublishedArtifactRequiresRecreatingTheTask()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var failure = DownloadActivityPresenter.CreateFailure(new OperationError(
                "download.validate.published-missing",
                "A published artifact is missing.",
                OperationErrorKind.Validation));
            var details = DownloadActivityPresenter.FormatFailureDetails(failure);

            Assert.False(failure.IsTransient);
            Assert.Contains("已发布的下载文件已缺失或损坏", details, StringComparison.Ordinal);
            Assert.Contains("移走或删除", details, StringComparison.Ordinal);
            Assert.Contains("重新解析并建立任务", details, StringComparison.Ordinal);
            Assert.DoesNotContain("点“重试”", details, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public Task RecreateTaskFailuresDoNotOfferAnIneffectiveRetry()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            AssertNotRetryable("download.media.recreate-task");
            AssertNotRetryable("download.resolve.recreate-task");
            AssertNotRetryable("download.media.contract");
            AssertNotRetryable("download.media.durl-audio-only");
        });
    }

    [AvaloniaFact]
    public Task CoverConversionFailureDoesNotOfferAnIneffectiveRetry()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var failure = DownloadActivityPresenter.CreateFailure(
                OperationError.Unexpected(
                    "download.cover.convert",
                    "The downloaded cover could not be converted."));
            var details = DownloadActivityPresenter.FormatFailureDetails(failure);

            Assert.False(failure.IsTransient);
            Assert.Contains("封面无法转换", details, StringComparison.Ordinal);
            Assert.Contains("取消“封面”", details, StringComparison.Ordinal);
            Assert.DoesNotContain("点“重试”", details, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public Task SubtitleIoFailuresExplainTheirDistinctRecoveryPaths()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var responseDetails = DownloadActivityPresenter.FormatFailureDetails(
                new DownKyi.Domain.Downloads.DownloadFailure(
                    DownloadArtifactWriter.SubtitleResponseReadFailureCode,
                    "下载失败",
                    true));
            var outputDetails = DownloadActivityPresenter.FormatFailureDetails(
                new DownKyi.Domain.Downloads.DownloadFailure(
                    DownloadArtifactWriter.SubtitleOutputWriteFailureCode,
                    "下载失败",
                    true));

            Assert.Contains("网络连接", responseDetails, StringComparison.Ordinal);
            Assert.DoesNotContain("下载目录权限", responseDetails, StringComparison.Ordinal);
            Assert.Contains("下载目录权限", outputDetails, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public Task SimilarLookingUnknownCodeDoesNotInheritARecoveryPath()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            foreach (var code in UnknownFailureCodes)
            {
                var failure = DownloadActivityPresenter.CreateFailure(OperationError.Unexpected(
                    code,
                    "Sensitive certificate details."));
                var details = DownloadActivityPresenter.FormatFailureDetails(failure);

                Assert.Equal("下载失败", failure.Message);
                Assert.Contains("暂时无法判断具体原因", details, StringComparison.Ordinal);
                Assert.DoesNotContain("下载目录权限", details, StringComparison.Ordinal);
                Assert.DoesNotContain("所选字幕", details, StringComparison.Ordinal);
                Assert.DoesNotContain("证书设置", details, StringComparison.Ordinal);
            }
        });
    }

    [AvaloniaFact]
    public Task ReviewFindingsUseTheirExactRecoveryMessage()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var expectations = new[]
            {
                ("download.validate.subtitle", "所选字幕"),
                ("download.validate.danmaku", "附加文件"),
                ("download.publish.reconcile", "文件发布的状态无法安全确认"),
                ("download.validate.published-missing", "原任务不能直接重试"),
                ("download.transfer.legacy-cleanup-failed", "旧下载器")
            };
            foreach (var (code, expectedText) in expectations)
            {
                var details = DownloadActivityPresenter.FormatFailureDetails(
                    new DownKyi.Domain.Downloads.DownloadFailure(code, "下载失败", false));
                Assert.Contains(expectedText, details, StringComparison.Ordinal);
            }

            var publicationDetails = DownloadActivityPresenter.FormatFailureDetails(
                new DownKyi.Domain.Downloads.DownloadFailure(
                    "download.publish.reconcile",
                    "下载失败",
                    false));
            Assert.Contains("删除此失败任务", publicationDetails, StringComparison.Ordinal);
            Assert.DoesNotContain("重新启动应用", publicationDetails, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public Task AmbiguousArtifactIoDoesNotClaimTheStorageIsAtFault()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            AssertAmbiguousArtifactIo("download.artifact.cover.io");
            AssertAmbiguousArtifactIo("download.artifact.danmaku.io");
        });
    }

    [AvaloniaFact]
    public Task UnknownDiagnosticCodeDoesNotDisplayUntrustedText()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var details = DownloadActivityPresenter.FormatFailureDetails(
                new DownKyi.Domain.Downloads.DownloadFailure(
                    "download.transfer.http-403?token=private-value",
                    "private-value",
                    true));

            Assert.Contains("错误代码: 未知", details, StringComparison.Ordinal);
            Assert.DoesNotContain("private-value", details, StringComparison.Ordinal);
        });
    }

    private static void AssertAmbiguousArtifactIo(string code)
    {
        var details = DownloadActivityPresenter.FormatFailureDetails(
            new DownKyi.Domain.Downloads.DownloadFailure(code, "下载失败", true));
        Assert.Contains("附加文件", details, StringComparison.Ordinal);
        Assert.DoesNotContain("本地下载文件无法写入", details, StringComparison.Ordinal);
    }

    private static void AssertNotRetryable(string code)
    {
        var failure = DownloadActivityPresenter.CreateFailure(
            OperationError.Unexpected(code, "The task must be recreated."));
        Assert.False(failure.IsTransient);
    }

}
