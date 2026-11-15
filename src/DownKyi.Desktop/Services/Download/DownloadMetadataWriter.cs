using System.Globalization;
using System.Xml;
using DownKyi.Application.Diagnostics;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;
using static DownKyi.Services.Download.DownloadArtifactFailure;

namespace DownKyi.Services.Download;

internal sealed class DownloadMetadataWriter(DownloadTaskStateWriter stateWriter, ILogger logger)
{
    public async Task<OperationResult<DownloadArtifactWriteResult>> GenerateNfoFileAsync(
        DownloadTaskId taskId,
        string nfoFile,
        DownloadNfoRequest metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nfoFile);
        ArgumentNullException.ThrowIfNull(metadata);

        try
        {
            await stateWriter.ClaimTransferFileAsync(
                taskId,
                "nfo",
                nfoFile,
                cancellationToken).ConfigureAwait(false);
            var writer = XmlWriter.Create(
                nfoFile,
                new XmlWriterSettings { Async = true, Indent = true });
            try
            {
                WriteMovieMetadata(writer, metadata);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            if (!DownloadFileIntegrity.Check(nfoFile).IsUsable)
            {
                return ArtifactFailure(
                    "download.artifact.nfo.invalid",
                    "The requested metadata output is missing or invalid.");
            }

            return OperationResult.Success(DownloadArtifactWriteResult.Created(nfoFile));
        }
        catch (IOException e)
        {
            logger.LogErrorMessage("NFO generation failed.", e);
            return ArtifactFailure(
                "download.artifact.nfo.io",
                "The requested metadata file could not be written.");
        }
        catch (UnauthorizedAccessException e)
        {
            logger.LogErrorMessage("NFO generation was denied.", e);
            return ArtifactFailure(
                "download.artifact.nfo.permission",
                "Permission was denied while writing the requested metadata file.");
        }
        catch (XmlException e)
        {
            logger.LogErrorMessage("NFO generation produced invalid XML.", e);
            return ArtifactFailure(
                "download.artifact.nfo.xml",
                "The requested metadata file could not be generated.");
        }
    }

    private static void WriteMovieMetadata(XmlWriter writer, DownloadNfoRequest metadata)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("movie");
        writer.WriteElementString("title", metadata.Title);
        writer.WriteElementString("plot", metadata.Plot);
        writer.WriteElementString("year", metadata.Year);

        foreach (var genre in metadata.Genres)
        {
            writer.WriteElementString("genre", genre);
        }

        foreach (var tag in metadata.Tags)
        {
            writer.WriteElementString("tag", tag);
        }

        foreach (var actor in metadata.Actors)
        {
            writer.WriteStartElement("actor");
            writer.WriteElementString("name", actor.Name);
            writer.WriteElementString("role", actor.Role);
            writer.WriteEndElement();
        }

        if (metadata.BilibiliId != null)
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", metadata.BilibiliId.Type);
            writer.WriteString(metadata.BilibiliId.Value);
            writer.WriteEndElement();
        }

        writer.WriteElementString("premiered", metadata.Premiered);
        foreach (var rating in metadata.Ratings)
        {
            writer.WriteStartElement("rating");
            writer.WriteAttributeString("name", rating.Name);
            writer.WriteAttributeString("max", rating.Max.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("default", rating.IsDefault ? "true" : "false");
            writer.WriteString(rating.Value.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }
}
