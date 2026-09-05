namespace DownKyi.CodeMetricsAudit;

internal sealed record AuditOptions(
    string RepositoryRoot,
    string SarifDirectory,
    string ClassificationFile,
    string OutputDirectory)
{
    public static AuditOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args.Count % 2 != 0)
        {
            throw new ArgumentException("Expected named path arguments for the CA1506 report generator.", nameof(args));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (!name.StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("CA1506 report arguments must be non-empty named values.", nameof(args));
            }

            if (!values.TryAdd(name, value))
            {
                throw new ArgumentException($"Duplicate CA1506 report argument: {name}", nameof(args));
            }
        }

        var repositoryRoot = ResolveRequiredPath(values, "--repository-root", mustBeDirectory: true);
        var sarifDirectory = ResolveRequiredPath(values, "--sarif-directory", mustBeDirectory: true);
        var classificationFile = ResolveRequiredPath(values, "--classification-file", mustBeDirectory: false);
        var outputValue = GetRequiredValue(values, "--output-directory");
        var outputDirectory = Path.GetFullPath(
            Path.IsPathRooted(outputValue)
                ? outputValue
                : Path.Combine(repositoryRoot, outputValue));

        if (values.Count != 4)
        {
            var unknown = values.Keys
                .Except(
                    ["--repository-root", "--sarif-directory", "--classification-file", "--output-directory"],
                    StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            throw new ArgumentException($"Unknown CA1506 report argument(s): {string.Join(", ", unknown)}", nameof(args));
        }

        return new AuditOptions(repositoryRoot, sarifDirectory, classificationFile, outputDirectory);
    }

    private static string ResolveRequiredPath(
        IReadOnlyDictionary<string, string> values,
        string name,
        bool mustBeDirectory)
    {
        var path = Path.GetFullPath(GetRequiredValue(values, name));
        var exists = mustBeDirectory ? Directory.Exists(path) : File.Exists(path);
        if (!exists)
        {
            throw new ArgumentException($"Required CA1506 audit path is missing: {path}", nameof(values));
        }

        return path;
    }

    private static string GetRequiredValue(IReadOnlyDictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var value))
        {
            throw new ArgumentException($"Missing CA1506 report argument: {name}", nameof(values));
        }

        return value;
    }
}
