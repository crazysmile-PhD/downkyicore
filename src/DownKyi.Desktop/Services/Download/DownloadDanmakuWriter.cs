using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.DanmakuApi;
using DownKyi.Core.Danmaku2Ass;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using static DownKyi.Services.Download.DownloadArtifactFailure;

using static DownKyi.Services.Download.DownloadArtifactIdentity;

namespace DownKyi.Services.Download;

internal sealed class DownloadDanmakuWriter(DownloadTaskStateWriter stateWriter, ILogger logger, IBilibiliApiClient client)
{
    public async Task<OperationResult<DownloadArtifactWriteResult>> DownloadDanmakuAsync(
        DownloadTaskId taskId,
        DownloadTaskMetadata metadata,
        IReadOnlyList<DownloadDanmakuOutputPath> outputs,
        DanmakuApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(settings);

        var assFile = outputs
            .Where(output => output.Kind == DownloadDanmakuOutputKind.Ass)
            .Select(static output => (string?)output.File)
            .SingleOrDefault();
        var xmlFile = outputs
            .Where(output => output.Kind == DownloadDanmakuOutputKind.Xml)
            .Select(static output => (string?)output.File)
            .SingleOrDefault();
        var outputFiles = outputs.Select(output => output.File).ToList();
        var outputClaims = outputs
            .Select(output => new KeyValuePair<string, string>(
                GetDanmakuTransferKey(output.Kind),
                output.File))
            .ToList();

        var subtitleConfig = new Config
        {
            Title = metadata.Name,
            ScreenWidth = settings.ScreenWidth,
            ScreenHeight = settings.ScreenHeight,
            FontName = settings.FontName,
            BaseFontSize = settings.FontSize,
            LineCount = settings.LineCount,
            LayoutAlgorithm = GetDanmakuLayoutAlgorithmValue(settings.LayoutAlgorithm),
            TuneDuration = 0,
            DropOffset = 0,
            BottomMargin = 0,
            CustomOffset = 0
        };

        var converter = new BilibiliDanmakuConverter()
            .SetTopFilter(settings.TopFilter == AllowStatus.Yes)
            .SetBottomFilter(settings.BottomFilter == AllowStatus.Yes)
            .SetScrollFilter(settings.ScrollFilter == AllowStatus.Yes)
            .SetCustomFilter(
                settings.RemoveEmojiAndSpecialCharacters == AllowStatus.Yes,
                settings.BlockedKeywords,
                settings.BlockedSenderUids);
        try
        {
            if (string.IsNullOrWhiteSpace(assFile) && string.IsNullOrWhiteSpace(xmlFile))
            {
                throw new ArgumentException("At least one danmaku output file is required.");
            }

            await stateWriter.ClaimTransferFilesAsync(
                taskId,
                outputClaims,
                cancellationToken).ConfigureAwait(false);

            var danmakus = await converter.GetDanmakusAsync(
                client,
                metadata.Media.Avid,
                metadata.Media.Cid,
                cancellationToken).ConfigureAwait(false);
            if (assFile != null)
            {
                var text = converter.CreateAss(subtitleConfig, danmakus, cancellationToken);
                await File.WriteAllTextAsync(assFile, text, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (xmlFile != null)
            {
                var xml = BilibiliDanmakuXmlWriter.CreateDocument(danmakus, cancellationToken);
                foreach (var invalidCharacter in xml.InvalidCharacters)
                {
                    var message =
                        "Skipped danmaku that XML 1.0 cannot represent. " +
                        $"dmid={invalidCharacter.Id.ToString(CultureInfo.InvariantCulture)}; " +
                        $"field={invalidCharacter.Field}; " +
                        $"invalid=U+{invalidCharacter.InvalidValue:X4}; " +
                        $"utf16Index={invalidCharacter.Utf16Index.ToString(CultureInfo.InvariantCulture)}; " +
                        $"category={invalidCharacter.Category}";
                    if (invalidCharacter.ContextBefore != null && invalidCharacter.ContextAfter != null)
                    {
                        message +=
                            $"; before={JsonConvert.ToString(invalidCharacter.ContextBefore)}" +
                            $"; after={JsonConvert.ToString(invalidCharacter.ContextAfter)}";
                    }
                    else
                    {
                        message += "; context=redacted";
                    }

                    logger.LogWarningMessage(message + ".");
                }

                var output = new FileStream(
                    xmlFile,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    await xml.Document.SaveAsync(output, SaveOptions.DisableFormatting, cancellationToken)
                        .ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (assFile != null && !DownloadFileIntegrity.Check(assFile).IsUsable ||
                xmlFile != null && !DownloadFileIntegrity.CheckXml(xmlFile, "i").IsUsable)
            {
                return ArtifactFailure(
                    "download.artifact.danmaku.invalid",
                    "The requested danmaku output is missing or invalid.");
            }

            return OperationResult.Success(DownloadArtifactWriteResult.Created(outputFiles));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            logger.LogErrorMessage("Danmaku download failed.", e);
            return ArtifactFailure(
                GetArtifactHttpFailureCode("danmaku", e.StatusCode),
                "The requested danmaku could not be downloaded.");
        }
        catch (InvalidProtocolBufferException e)
        {
            logger.LogErrorMessage("Danmaku response parsing failed.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.parse",
                "The requested danmaku response was invalid.");
        }
        catch (InvalidDataException e)
        {
            logger.LogErrorMessage("Danmaku response contract validation failed.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.parse",
                "The requested danmaku response was invalid.");
        }
        catch (XmlException e)
        {
            logger.LogErrorMessage("Danmaku XML generation failed.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.xml",
                "The requested danmaku XML could not be generated.");
        }
        catch (ArgumentException e)
        {
            logger.LogErrorMessage("Danmaku output contained invalid data.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.data",
                "The requested danmaku output contained invalid data.");
        }
        catch (IOException e)
        {
            logger.LogErrorMessage("Danmaku conversion or write failed.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.io",
                "The requested danmaku could not be converted or written.");
        }
        catch (UnauthorizedAccessException e)
        {
            logger.LogErrorMessage("Danmaku output was denied.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.permission",
                "Permission was denied while writing the requested danmaku.");
        }
    }

    private static string GetDanmakuLayoutAlgorithmValue(DanmakuLayoutAlgorithm algorithm)
    {
        return algorithm switch
        {
            DanmakuLayoutAlgorithm.None => "none",
            DanmakuLayoutAlgorithm.Async => "async",
            DanmakuLayoutAlgorithm.Sync => "sync",
            _ => throw new ArgumentOutOfRangeException(
                nameof(algorithm),
                algorithm,
                "Unsupported danmaku layout algorithm.")
        };
    }
}
