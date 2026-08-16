namespace DownKyi.Application.Downloads;

public sealed class FileSystemOutputIdentityProvider : IOutputIdentityProvider
{
    public static FileSystemOutputIdentityProvider Instance { get; } = new();

    private FileSystemOutputIdentityProvider()
    {
    }

    public string CreateReservationKey(
        string basePath,
        bool ignoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        var logicalPath =
            DownloadOutputPathKey.NormalizeLogicalPath(
                basePath);

        var physicalPath =
            ResolveExistingAliases(
                logicalPath);

        return ignoreCase
            ? physicalPath.ToUpperInvariant()
            : physicalPath;
    }

    private static string ResolveExistingAliases(
        string fullPath)
    {
        var root =
            Path.GetPathRoot(
                fullPath);

        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var relative =
            Path.GetRelativePath(
                root,
                fullPath);

        if (relative == ".")
        {
            return fullPath;
        }

        var segments =
            relative.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);

        var current =
            root;

        for (var index = 0;
             index < segments.Length;
             index++)
        {
            var candidate =
                Path.Combine(
                    current,
                    segments[index]);

            if (Directory.Exists(candidate))
            {
                var directory =
                    new DirectoryInfo(
                        candidate);

                var target =
                    directory.ResolveLinkTarget(
                        returnFinalTarget: true);

                current =
                    target?.FullName
                    ?? directory.FullName;

                continue;
            }

            if (File.Exists(candidate))
            {
                if (index != segments.Length - 1)
                {
                    throw new IOException(
                        $"A file blocks output path traversal: '{candidate}'.");
                }

                var file =
                    new FileInfo(
                        candidate);

                var target =
                    file.ResolveLinkTarget(
                        returnFinalTarget: true);

                current =
                    target?.FullName
                    ?? file.FullName;

                continue;
            }

            // The remaining path does not exist yet. Its identity is rooted
            // underneath the already resolved physical ancestor.
            for (var remaining = index;
                 remaining < segments.Length;
                 remaining++)
            {
                current =
                    Path.Combine(
                        current,
                        segments[remaining]);
            }

            break;
        }

        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                current));
    }
}
