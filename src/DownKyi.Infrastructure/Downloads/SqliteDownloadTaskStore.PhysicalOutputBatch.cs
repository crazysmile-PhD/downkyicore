using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;

namespace DownKyi.Infrastructure.Downloads;

public sealed partial class SqliteDownloadTaskStore
{
    private static bool HasPhysicalOutputCollision(
        IReadOnlyList<DownloadTask> tasks)
    {
        var comparer =
            DownloadOutputPathKey.UsesCaseInsensitiveComparison
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        var namesByDirectory =
            new Dictionary<string, HashSet<string>>(
                comparer);

        foreach (var task in tasks)
        {
            if (task.Phase == DownloadPhase.Completed)
            {
                continue;
            }

            var basePath =
                DownloadOutputPathKey.NormalizeLogicalPath(
                    task.Output.BasePath);

            var directory =
                Path.GetDirectoryName(basePath);

            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            if (!namesByDirectory.TryGetValue(
                    directory,
                    out var names))
            {
                names =
                    new HashSet<string>(comparer);

                namesByDirectory.Add(
                    directory,
                    names);
            }

            names.Add(
                Path.GetFileName(basePath));
        }

        foreach (var pair in namesByDirectory)
        {
            if (!Directory.Exists(pair.Key))
            {
                continue;
            }

            foreach (var file in
                     Directory.EnumerateFiles(pair.Key))
            {
                var physicalBaseName =
                    Path.GetFileNameWithoutExtension(
                        file);

                if (pair.Value.Contains(
                        physicalBaseName))
                {
                    return true;
                }
            }
        }

        return false;
    }
}