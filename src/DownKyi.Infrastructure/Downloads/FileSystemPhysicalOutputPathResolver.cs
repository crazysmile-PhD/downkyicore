using DownKyi.Application.Downloads;

namespace DownKyi.Infrastructure.Downloads;

public sealed class FileSystemPhysicalOutputPathResolver : IPhysicalOutputPathResolver
{
    private const int MaximumLinkDepth = 40;
    private const string ResolutionFailureMessage =
        "Unable to resolve physical output path.";

    public string ResolvePhysicalBasePath(string logicalBasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalBasePath);

        try
        {
            var fullStemPath = Path.TrimEndingDirectorySeparator(
                MakeFullyQualifiedPath(logicalBasePath));
            var directoryPath = Path.GetDirectoryName(fullStemPath);
            var stem = Path.GetFileName(fullStemPath);
            return string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(stem)
                ? fullStemPath
                : Path.Combine(
                    ResolveDirectoryAncestors(directoryPath, new ResolutionState()),
                    stem);
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
        var fullPath = MakeFullyQualifiedPath(directoryPath);
        state.EnterDirectoryPath(fullPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException(ResolutionFailureMessage);
        }

        EnsureDirectoryRoot(root);
        var relativePath = fullPath[root.Length..];
        if (relativePath.Length == 0)
        {
            return root;
        }

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var unresolvedComponentDepth = 0;
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index] == ".")
            {
                continue;
            }

            if (segments[index] == "..")
            {
                current = MoveToParent(current, root);
                if (unresolvedComponentDepth > 0)
                {
                    unresolvedComponentDepth--;
                }

                continue;
            }

            if (unresolvedComponentDepth > 0)
            {
                current = Path.Combine(current, segments[index]);
                unresolvedComponentDepth++;
                continue;
            }

            var candidate = Path.Combine(current, segments[index]);
            var linkTarget = new DirectoryInfo(candidate).LinkTarget;
            if (linkTarget != null)
            {
                state.RecordLink();
                var redirectedPath = ResolveLinkTargetPath(candidate, linkTarget);
                for (var remaining = index + 1; remaining < segments.Length; remaining++)
                {
                    redirectedPath = Path.Combine(redirectedPath, segments[remaining]);
                }

                return ResolveDirectoryAncestors(redirectedPath, state);
            }

            if (!TryReadAttributes(candidate, out var attributes))
            {
                current = candidate;
                unresolvedComponentDepth = 1;
                continue;
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

    private static string MakeFullyQualifiedPath(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return Path.Combine(Environment.CurrentDirectory, path);
        }

        var absoluteRoot = Path.GetFullPath(root);
        return Path.Combine(absoluteRoot, path[root.Length..]);
    }

    private static string ResolveLinkTargetPath(string linkPath, string linkTarget)
    {
        if (Path.IsPathFullyQualified(linkTarget))
        {
            return linkTarget;
        }

        var parent = Path.GetDirectoryName(linkPath);
        if (string.IsNullOrEmpty(parent))
        {
            throw new IOException(ResolutionFailureMessage);
        }

        if (!Path.IsPathRooted(linkTarget))
        {
            return Path.Combine(parent, linkTarget);
        }

        var linkRoot = Path.GetPathRoot(linkTarget);
        var parentRoot = Path.GetPathRoot(parent);
        if (string.IsNullOrEmpty(linkRoot) || string.IsNullOrEmpty(parentRoot))
        {
            throw new IOException(ResolutionFailureMessage);
        }

        return Path.Combine(parentRoot, linkTarget[linkRoot.Length..]);
    }

    private static string MoveToParent(string path, string root)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(parent) ? root : parent;
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
        private readonly HashSet<string> _visitedDirectoryStates = new(StringComparer.Ordinal);
        private int _linkDepth;

        public void EnterDirectoryPath(string directoryPath)
        {
            if (!_visitedDirectoryStates.Add(directoryPath))
            {
                throw new IOException(ResolutionFailureMessage);
            }
        }

        public void RecordLink()
        {
            if (_linkDepth >= MaximumLinkDepth)
            {
                throw new IOException(ResolutionFailureMessage);
            }

            _linkDepth++;
        }
    }
}
