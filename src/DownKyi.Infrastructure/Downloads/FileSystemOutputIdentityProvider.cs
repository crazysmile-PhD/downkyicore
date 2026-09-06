using DownKyi.Application.Downloads;

namespace DownKyi.Infrastructure.Downloads;

public sealed class FileSystemOutputIdentityProvider : IOutputIdentityProvider
{
    private const int MaximumLinkDepth = 40;
    private const string ResolutionFailureMessage =
        "Unable to resolve output filesystem identity.";

    public string CreateReservationKey(string basePath, bool ignoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        try
        {
            var fullStemPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
            var directoryPath = Path.GetDirectoryName(fullStemPath);
            var stem = Path.GetFileName(fullStemPath);
            var physicalStemPath = string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(stem)
                ? fullStemPath
                : Path.Combine(
                    ResolveDirectoryAncestors(directoryPath, new ResolutionState()),
                    stem);
            var normalized = DownloadOutputPathKey.NormalizeLogicalPath(physicalStemPath);
            return ignoreCase ? normalized.ToUpperInvariant() : normalized;
        }
        catch (Exception exception) when (IsTopologyException(exception))
        {
            throw new IOException(ResolutionFailureMessage);
        }
    }

    private static string ResolveDirectoryAncestors(
        string directoryPath,
        ResolutionState state)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException(ResolutionFailureMessage);
        }

        EnsureDirectoryRoot(root);
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
        {
            return root;
        }

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            var linkTarget = new DirectoryInfo(candidate).LinkTarget;
            if (linkTarget != null)
            {
                state.RecordLink(candidate);
                var redirectedPath = ResolveLinkTargetPath(candidate, linkTarget);
                for (var remaining = index + 1; remaining < segments.Length; remaining++)
                {
                    redirectedPath = Path.Combine(redirectedPath, segments[remaining]);
                }

                return ResolveDirectoryAncestors(redirectedPath, state);
            }

            if (!TryReadAttributes(candidate, out var attributes))
            {
                for (var remaining = index; remaining < segments.Length; remaining++)
                {
                    current = Path.Combine(current, segments[remaining]);
                }

                return current;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException(ResolutionFailureMessage);
            }

            current = candidate;
        }

        return current;
    }

    private static string ResolveLinkTargetPath(string linkPath, string linkTarget)
    {
        if (Path.IsPathFullyQualified(linkTarget))
        {
            return Path.GetFullPath(linkTarget);
        }

        var parent = Path.GetDirectoryName(linkPath);
        if (string.IsNullOrEmpty(parent))
        {
            throw new IOException(ResolutionFailureMessage);
        }

        return Path.GetFullPath(Path.Combine(parent, linkTarget));
    }

    private static void EnsureDirectoryRoot(string root)
    {
        var attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException(ResolutionFailureMessage);
        }
    }

    private static bool TryReadAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsTopologyException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException
            or ArgumentException;
    }

    private sealed class ResolutionState
    {
        private readonly HashSet<string> _visitedLinks = new(
            DownloadOutputPathKey.UsesCaseInsensitiveComparison
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        public void RecordLink(string linkPath)
        {
            if (_visitedLinks.Count >= MaximumLinkDepth ||
                !_visitedLinks.Add(DownloadOutputPathKey.NormalizeLogicalPath(linkPath)))
            {
                throw new IOException(ResolutionFailureMessage);
            }
        }
    }
}
