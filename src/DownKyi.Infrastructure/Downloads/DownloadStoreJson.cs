using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DownKyi.Domain.Downloads;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreJson
{
    public static string WriteBooleanMap(IEnumerable<KeyValuePair<string, bool>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Write(writer =>
        {
            writer.WriteStartObject();
            foreach (var value in values)
            {
                writer.WriteBoolean(value.Key, value.Value);
            }

            writer.WriteEndObject();
        });
    }

    public static string WriteStringMap(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Write(writer =>
        {
            writer.WriteStartObject();
            foreach (var value in values)
            {
                writer.WriteString(value.Key, value.Value);
            }

            writer.WriteEndObject();
        });
    }

    public static string WriteStringList(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Write(writer =>
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        });
    }

    public static string WriteQuality(DownloadQuality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("Name", quality.Name);
            writer.WriteNumber("Id", quality.Id);
            writer.WriteEndObject();
        });
    }

    public static string WriteNfoRequest(DownloadNfoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(new NfoRequestPayload
        {
            Version = 1,
            Title = request.Title,
            Plot = request.Plot,
            Year = request.Year,
            Genres = request.Genres.ToArray(),
            Tags = request.Tags.ToArray(),
            Actors = request.Actors.Select(actor => new NfoActorPayload
            {
                Name = actor.Name,
                Role = actor.Role
            }).ToArray(),
            BilibiliId = request.BilibiliId == null
                ? null
                : new NfoUniqueIdPayload
                {
                    Type = request.BilibiliId.Type,
                    Value = request.BilibiliId.Value
                },
            Premiered = request.Premiered,
            Ratings = request.Ratings.Select(rating => new NfoRatingPayload
            {
                Name = rating.Name,
                Value = rating.Value,
                Max = rating.Max,
                IsDefault = rating.IsDefault
            }).ToArray()
        });
    }

    public static ImmutableDictionary<string, bool> ReadBooleanMap(string json, string fieldName)
    {
        return Read(json, fieldName, root =>
        {
            RequireKind(root, JsonValueKind.Object, fieldName);
            var builder = ImmutableDictionary.CreateBuilder<string, bool>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw Corrupt(fieldName, $"Property '{property.Name}' is not a boolean.");
                }

                builder.Add(property.Name, property.Value.GetBoolean());
            }

            return builder.ToImmutable();
        });
    }

    public static ImmutableDictionary<string, string> ReadStringMap(string json, string fieldName)
    {
        return Read(json, fieldName, root =>
        {
            RequireKind(root, JsonValueKind.Object, fieldName);
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw Corrupt(fieldName, $"Property '{property.Name}' is not a string.");
                }

                builder.Add(property.Name, property.Value.GetString()!);
            }

            return builder.ToImmutable();
        });
    }

    public static ImmutableArray<string> ReadStringList(string json, string fieldName)
    {
        return Read(json, fieldName, root =>
        {
            RequireKind(root, JsonValueKind.Array, fieldName);
            var builder = ImmutableArray.CreateBuilder<string>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw Corrupt(fieldName, "Array contains a non-string value.");
                }

                builder.Add(item.GetString()!);
            }

            return builder.ToImmutable();
        });
    }

    public static DownloadQuality ReadQuality(string? json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DownloadQuality(0, string.Empty);
        }

        return Read(json, fieldName, root =>
        {
            RequireKind(root, JsonValueKind.Object, fieldName);
            var id = 0;
            var name = string.Empty;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals("Id", StringComparison.OrdinalIgnoreCase))
                {
                    if (!property.Value.TryGetInt32(out id))
                    {
                        throw Corrupt(fieldName, "Quality Id is not an integer.");
                    }
                }
                else if (property.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw Corrupt(fieldName, "Quality Name is not a string.");
                    }

                    name = property.Value.GetString()!;
                }
            }

            return new DownloadQuality(id, name);
        });
    }

    public static DownloadNfoRequest ReadNfoRequest(string json, string fieldName)
    {
        return Read(json, fieldName, root =>
        {
            var payload = root.Deserialize<NfoRequestPayload>()
                          ?? throw Corrupt(fieldName, "Stored NFO request is null.");
            if (payload.Version != 1)
            {
                throw Corrupt(fieldName, $"Unsupported NFO request version {payload.Version}.");
            }

            if (payload.Title == null || payload.Plot == null || payload.Year == null
                || payload.Genres == null || payload.Genres.Any(value => value == null)
                || payload.Tags == null || payload.Tags.Any(value => value == null)
                || payload.Actors == null || payload.Actors.Any(actor =>
                    actor == null || actor.Name == null || actor.Role == null)
                || payload.BilibiliId is { Type: null } or { Value: null }
                || payload.Premiered == null
                || payload.Ratings == null || payload.Ratings.Any(rating =>
                    rating == null || rating.Name == null))
            {
                throw Corrupt(fieldName, "Stored NFO request has a null required value.");
            }

            return new DownloadNfoRequest(
                payload.Title!,
                payload.Plot!,
                payload.Year!,
                payload.Genres!.Select(value => value!).ToImmutableArray(),
                payload.Tags!.Select(value => value!).ToImmutableArray(),
                payload.Actors.Select(actor => new DownloadNfoActor(actor!.Name!, actor.Role!))
                    .ToImmutableArray(),
                payload.BilibiliId == null
                    ? null
                    : new DownloadNfoUniqueId(payload.BilibiliId.Type!, payload.BilibiliId.Value!),
                payload.Premiered!,
                payload.Ratings.Select(rating => new DownloadNfoRating(
                    rating!.Name!,
                    rating.Value,
                    rating.Max,
                    rating.IsDefault)).ToImmutableArray());
        });
    }

    private static T Read<T>(string json, string fieldName, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            return read(document.RootElement);
        }
        catch (DownloadRecordCorruptException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Corrupt(fieldName, "Stored JSON is malformed.", exception);
        }
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void RequireKind(JsonElement element, JsonValueKind expected, string fieldName)
    {
        if (element.ValueKind != expected)
        {
            throw Corrupt(fieldName, $"Expected JSON {expected}, found {element.ValueKind}.");
        }
    }

    private static DownloadRecordCorruptException Corrupt(
        string fieldName,
        string reason,
        Exception? innerException = null)
    {
        return new DownloadRecordCorruptException(fieldName, reason, innerException);
    }

    private sealed class NfoRequestPayload
    {
        public required int Version { get; init; }

        public required string? Title { get; init; }

        public required string? Plot { get; init; }

        public required string? Year { get; init; }

        public required string?[]? Genres { get; init; }

        public required string?[]? Tags { get; init; }

        public required NfoActorPayload?[]? Actors { get; init; }

        public NfoUniqueIdPayload? BilibiliId { get; init; }

        public required string? Premiered { get; init; }

        public required NfoRatingPayload?[]? Ratings { get; init; }
    }

    private sealed class NfoActorPayload
    {
        public required string? Name { get; init; }

        public required string? Role { get; init; }
    }

    private sealed class NfoUniqueIdPayload
    {
        public required string? Type { get; init; }

        public required string? Value { get; init; }
    }

    private sealed class NfoRatingPayload
    {
        public required string? Name { get; init; }

        public required float Value { get; init; }

        public required int Max { get; init; }

        public required bool IsDefault { get; init; }
    }
}
