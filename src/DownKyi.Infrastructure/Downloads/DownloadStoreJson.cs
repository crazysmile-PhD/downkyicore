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
}
