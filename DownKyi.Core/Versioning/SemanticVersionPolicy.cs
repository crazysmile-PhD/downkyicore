using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NuGet.Versioning;

namespace DownKyi.Core.Versioning;

public static class SemanticVersionPolicy
{
    public static string NormalizeForDisplay(string? value)
    {
        return TryParse(value, out var version)
            ? Format(version, includeMetadata: false)
            : string.Empty;
    }

    public static bool TryNormalizeIdentity(string? value, out string normalized)
    {
        if (!TryParse(value, out var version))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = Format(version, includeMetadata: true);
        return true;
    }

    public static bool IsNewer(string? candidate, string? current)
    {
        return TryParse(candidate, out var candidateVersion) &&
               TryParse(current, out var currentVersion) &&
               VersionComparer.VersionRelease.Compare(candidateVersion, currentVersion) > 0;
    }

    public static bool HasSamePrecedence(string? left, string? right)
    {
        return TryParse(left, out var leftVersion) &&
               TryParse(right, out var rightVersion) &&
               VersionComparer.VersionRelease.Equals(leftVersion, rightVersion);
    }

    internal static bool TryParse(
        string? value,
        [NotNullWhen(true)] out NuGetVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length > 1 &&
            (candidate[0] == 'v' || candidate[0] == 'V') &&
            char.IsAsciiDigit(candidate[1]))
        {
            candidate = candidate[1..];
        }

        var coreEnd = candidate.IndexOfAny('-', '+');
        var core = coreEnd < 0 ? candidate : candidate[..coreEnd];
        var coreParts = core.Split('.');
        if (coreParts.Length != 3 || coreParts.Any(part =>
                part.Length == 0 ||
                (part.Length > 1 && part[0] == '0') ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        return NuGetVersion.TryParse(candidate, out version);
    }

    private static string Format(NuGetVersion version, bool includeMetadata)
    {
        var normalized = string.Create(
            CultureInfo.InvariantCulture,
            $"{version.Major}.{version.Minor}.{version.Patch}");
        if (!string.IsNullOrEmpty(version.Release))
        {
            normalized = $"{normalized}-{version.Release}";
        }
        if (includeMetadata && !string.IsNullOrEmpty(version.Metadata))
        {
            normalized = $"{normalized}+{version.Metadata}";
        }

        return normalized;
    }
}
