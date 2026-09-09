using System.Collections.Immutable;

namespace DownKyi.Domain.Downloads;

public sealed class DownloadPlan
{
    public DownloadPlan(
        IEnumerable<KeyValuePair<string, bool>> requestedAssets,
        IEnumerable<KeyValuePair<string, string>> transferFiles,
        int streamType,
        DownloadNfoRequest? nfoRequest)
    {
        ArgumentNullException.ThrowIfNull(requestedAssets);
        ArgumentNullException.ThrowIfNull(transferFiles);

        RequestedAssets = requestedAssets.ToImmutableDictionary(StringComparer.Ordinal);
        TransferFiles = transferFiles.ToImmutableDictionary(StringComparer.Ordinal);
        StreamType = streamType;
        NfoRequest = nfoRequest;
    }

    public ImmutableDictionary<string, bool> RequestedAssets { get; }

    public ImmutableDictionary<string, string> TransferFiles { get; }

    public int StreamType { get; }

    public DownloadNfoRequest? NfoRequest { get; }
}

public sealed class DownloadNfoRequest
{
    public DownloadNfoRequest(
        string title,
        string plot,
        string year,
        ImmutableArray<string> genres,
        ImmutableArray<string> tags,
        ImmutableArray<DownloadNfoActor> actors,
        DownloadNfoUniqueId? bilibiliId,
        string premiered,
        ImmutableArray<DownloadNfoRating> ratings)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(plot);
        ArgumentNullException.ThrowIfNull(year);
        ArgumentNullException.ThrowIfNull(premiered);
        RequireInitialized(genres, nameof(genres));
        RequireInitialized(tags, nameof(tags));
        RequireInitialized(actors, nameof(actors));
        RequireInitialized(ratings, nameof(ratings));
        if (genres.Any(static value => value is null))
        {
            throw new ArgumentException("Genres cannot contain null values.", nameof(genres));
        }

        if (tags.Any(static value => value is null))
        {
            throw new ArgumentException("Tags cannot contain null values.", nameof(tags));
        }

        if (actors.Any(static actor => actor is null || actor.Name is null || actor.Role is null))
        {
            throw new ArgumentException("Actors must contain complete values.", nameof(actors));
        }

        if (bilibiliId is { Type: null } or { Value: null })
        {
            throw new ArgumentException("The Bilibili ID must be complete.", nameof(bilibiliId));
        }

        if (ratings.Any(static rating => rating is null || rating.Name is null))
        {
            throw new ArgumentException("Ratings must contain complete values.", nameof(ratings));
        }

        Title = title;
        Plot = plot;
        Year = year;
        Genres = genres;
        Tags = tags;
        Actors = actors;
        BilibiliId = bilibiliId;
        Premiered = premiered;
        Ratings = ratings;
    }

    public string Title { get; }

    public string Plot { get; }

    public string Year { get; }

    public ImmutableArray<string> Genres { get; }

    public ImmutableArray<string> Tags { get; }

    public ImmutableArray<DownloadNfoActor> Actors { get; }

    public DownloadNfoUniqueId? BilibiliId { get; }

    public string Premiered { get; }

    public ImmutableArray<DownloadNfoRating> Ratings { get; }

    private static void RequireInitialized<T>(ImmutableArray<T> values, string parameterName)
    {
        if (values.IsDefault)
        {
            throw new ArgumentException("The immutable array must be initialized.", parameterName);
        }
    }
}

public sealed record DownloadNfoActor(string Name, string Role);

public sealed record DownloadNfoUniqueId(string Type, string Value);

public sealed record DownloadNfoRating(string Name, float Value, int Max, bool IsDefault);
