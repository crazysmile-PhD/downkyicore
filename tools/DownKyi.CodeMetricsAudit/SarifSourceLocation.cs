using System.Text.Json;

namespace DownKyi.CodeMetricsAudit;

internal sealed record SarifSourceLocation(string File, int Line, int Column)
{
    public static SarifSourceLocation Read(string repositoryRoot, JsonElement result)
    {
        if (!result.TryGetProperty("locations", out var locations) ||
            locations.ValueKind != JsonValueKind.Array ||
            locations.GetArrayLength() == 0)
        {
            throw new InvalidDataException("CA1506 result has no source location.");
        }

        var location = locations[0];
        JsonElement physical;
        if (!location.TryGetProperty("resultFile", out physical) &&
            !location.TryGetProperty("physicalLocation", out physical))
        {
            throw new InvalidDataException("CA1506 result has no physical source location.");
        }

        string? uriText = null;
        if (physical.TryGetProperty("uri", out var uri))
        {
            uriText = uri.GetString();
        }
        else if (physical.TryGetProperty("artifactLocation", out var artifact) &&
                 artifact.TryGetProperty("uri", out uri))
        {
            uriText = uri.GetString();
        }
        if (string.IsNullOrWhiteSpace(uriText) || !Uri.TryCreate(uriText, UriKind.Absolute, out var sourceUri))
        {
            throw new InvalidDataException("CA1506 result has no valid source URI.");
        }
        if (!physical.TryGetProperty("region", out var region) ||
            !region.TryGetProperty("startLine", out var line) ||
            !region.TryGetProperty("startColumn", out var column))
        {
            throw new InvalidDataException("CA1506 result has no complete source region.");
        }

        var sourcePath = Path.GetFullPath(sourceUri.LocalPath);
        var relativePath = Path.GetRelativePath(repositoryRoot, sourcePath).Replace('\\', '/');
        if (relativePath == ".." ||
            relativePath.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"CA1506 result resolved outside the repository: {uriText}");
        }

        return new SarifSourceLocation(relativePath, line.GetInt32(), column.GetInt32());
    }
}
