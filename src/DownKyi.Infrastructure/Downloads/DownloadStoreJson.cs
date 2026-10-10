using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DownKyi.Domain.Downloads;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreJson
{
    private const string SelectedSubtitleTrackIds = "selectedSubtitleTrackIds";
    private const string DefaultSubtitleTrackId = "defaultSubtitleTrackId";
    private const string MediaKind = "mediaKind";
    private const string DanmakuOutputFormat = "danmakuOutputFormat";

    public static string WriteContentSelection(DownloadContentSelection value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var payload = value.ToLegacyMap()
            .ToDictionary(item => item.Key, item => (object)item.Value, StringComparer.Ordinal);
        if (value.SelectedSubtitleTrackIds is { } selectedIds)
        {
            payload[SelectedSubtitleTrackIds] = selectedIds;
        }

        if (value.DefaultSubtitleTrackId is { } defaultId)
        {
            payload[DefaultSubtitleTrackId] = defaultId;
        }

        if (value.MediaKind is { } mediaKind)
        {
            payload[MediaKind] = WriteMediaKind(mediaKind);
        }

        if (value.DanmakuOutputFormat is { } danmakuOutputFormat)
        {
            payload[DanmakuOutputFormat] = (int)danmakuOutputFormat;
        }

        return JsonSerializer.Serialize(payload);
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
                if (property.NameEquals(SelectedSubtitleTrackIds) ||
                    property.NameEquals(DefaultSubtitleTrackId) ||
                    property.NameEquals(MediaKind) ||
                    property.NameEquals(DanmakuOutputFormat))
                {
                    continue;
                }

                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw Corrupt(fieldName, $"Property '{property.Name}' is not a boolean.");
                }

                builder.Add(property.Name, property.Value.GetBoolean());
            }

            return builder.ToImmutable();
        });
    }

    public static DownloadContentSelection ReadContentSelection(string json, string fieldName)
    {
        var result = DownloadContentSelection.FromLegacyMap(ReadBooleanMap(json, fieldName));
        return Read(json, fieldName, root =>
        {
            var selectedIds = root.TryGetProperty(SelectedSubtitleTrackIds, out var selected)
                ? selected.Deserialize<long[]>()?.ToImmutableArray()
                  ?? throw Corrupt(fieldName, "Selected subtitle track ids are null.")
                : (ImmutableArray<long>?)null;
            var defaultId = root.TryGetProperty(DefaultSubtitleTrackId, out var defaultTrack)
                ? defaultTrack.Deserialize<long>()
                : (long?)null;
            var mediaKind = root.TryGetProperty(MediaKind, out var storedMediaKind)
                ? ReadMediaKind(storedMediaKind, fieldName)
                : (DownloadMediaKind?)null;
            var danmakuOutputFormat = root.TryGetProperty(
                DanmakuOutputFormat,
                out var storedDanmakuOutputFormat)
                ? ReadDanmakuOutputFormat(storedDanmakuOutputFormat, fieldName)
                : (DownloadDanmakuOutputFormat?)null;

            if (defaultId is { } selectedDefault &&
                (selectedIds is not { } ids || !ids.Contains(selectedDefault)))
            {
                throw Corrupt(fieldName, "Default subtitle track is not selected.");
            }

            return result with
            {
                MediaKind = mediaKind,
                SelectedSubtitleTrackIds = selectedIds,
                DefaultSubtitleTrackId = defaultId,
                DanmakuOutputFormat = danmakuOutputFormat
            };
        });
    }

    private static DownloadDanmakuOutputFormat ReadDanmakuOutputFormat(
        JsonElement value,
        string fieldName)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var storedValue))
        {
            throw Corrupt(fieldName, "Danmaku output format is not an integer.");
        }

        var format = (DownloadDanmakuOutputFormat)storedValue;
        const DownloadDanmakuOutputFormat supported =
            DownloadDanmakuOutputFormat.Ass | DownloadDanmakuOutputFormat.Xml;
        if (format == DownloadDanmakuOutputFormat.None || (format & ~supported) != 0)
        {
            throw Corrupt(fieldName, "Danmaku output format is unsupported.");
        }

        return format;
    }

    private static string WriteMediaKind(DownloadMediaKind mediaKind)
    {
        return mediaKind switch
        {
            DownloadMediaKind.None => "none",
            DownloadMediaKind.Dash => "dash",
            DownloadMediaKind.Durl => "durl",
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind), mediaKind, "Unsupported media kind.")
        };
    }

    private static DownloadMediaKind ReadMediaKind(JsonElement value, string fieldName)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Corrupt(fieldName, "Media kind is not a string.");
        }

        return value.GetString() switch
        {
            "none" => DownloadMediaKind.None,
            "dash" => DownloadMediaKind.Dash,
            "durl" => DownloadMediaKind.Durl,
            var unsupported => throw Corrupt(
                fieldName,
                $"Unsupported media kind '{unsupported ?? "null"}'.")
        };
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
