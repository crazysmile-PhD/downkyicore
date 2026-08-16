using System.Globalization;
using System.Xml;
using DownKyi.Application.Bilibili;
using DownKyi.Core.Danmaku2Ass;
using DownKyi.Domain.Results;
using DownKyi.Models;

namespace DownKyi.Services.Download;

internal sealed partial class DownloadArtifactWriter
{
    private Task<AtomicOutputPublishResult> PublishCoverAsync(
        string fileName,
        string coverUrl,
        CancellationToken cancellationToken)
    {
        return _outputPublisher.PublishAsync(
            fileName,
            (temporaryPath, token) => _client.DownloadFileAsync(
                new BilibiliHttpRequest(coverUrl),
                temporaryPath,
                token),
            cancellationToken);
    }

    private Task<AtomicOutputPublishResult> PublishDanmakuAsync(
        string assFile,
        BilibiliDanmakuConverter converter,
        DownloadBase downloadBase,
        Config subtitleConfig,
        CancellationToken cancellationToken)
    {
        return _outputPublisher.PublishAsync(
            assFile,
            (temporaryPath, token) => converter.CreateAsync(
                _client,
                downloadBase.Avid,
                downloadBase.Cid,
                subtitleConfig,
                temporaryPath,
                token),
            cancellationToken);
    }

    private Task<AtomicOutputPublishResult> PublishSubtitleAsync(
        string srtFile,
        string content,
        CancellationToken cancellationToken)
    {
        return _outputPublisher.PublishAsync(
            srtFile,
            (temporaryPath, token) => File.WriteAllTextAsync(temporaryPath, content, token),
            cancellationToken);
    }

    private Task<AtomicOutputPublishResult> PublishDefaultSubtitleAsync(
        string defaultSubtitleFile,
        string sourceSubtitleFile,
        CancellationToken cancellationToken)
    {
        return _outputPublisher.PublishAsync(
            defaultSubtitleFile,
            (temporaryPath, _) =>
            {
                File.Copy(sourceSubtitleFile, temporaryPath, overwrite: true);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private Task<AtomicOutputPublishResult> PublishNfoAsync(
        string nfoFile,
        MovieMetadata metadata,
        CancellationToken cancellationToken)
    {
        return _outputPublisher.PublishAsync(
            nfoFile,
            async (temporaryPath, _) =>
            {
                var writer = CreateNfoWriter(temporaryPath);
                try
                {
                    WriteMovieMetadata(writer, metadata);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                finally
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    private static OperationResult<DownloadArtifactWriteResult> OutputCollisionFailure()
    {
        return ArtifactFailure(
            "download.output.destination-collision",
            "The output destination is already occupied by another file.");
    }

    private static void WriteMovieMetadata(XmlWriter writer, MovieMetadata metadata)
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
