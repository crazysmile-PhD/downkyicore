using System.Text.Json;

namespace DownKyi.CodeMetricsAudit;

internal static class Ca1506ReportGenerator
{
    internal static readonly string[] ClassificationOrder =
    [
        "architecture hotspot",
        "composition root",
        "test integration",
        "framework-driven",
        "needs manual review"
    ];

    public static Ca1506Report Generate(
        string repositoryRoot,
        string sarifDirectory,
        string classificationFile,
        GitState gitState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sarifDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(classificationFile);
        ArgumentNullException.ThrowIfNull(gitState);

        var sarifFiles = Directory
            .EnumerateFiles(sarifDirectory, "*.sarif", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (sarifFiles.Length == 0)
        {
            throw new InvalidDataException("CA1506 audit build produced no SARIF reports.");
        }

        var classifications = ReadClassifications(classificationFile);
        var findingsByKey = new Dictionary<string, Ca1506Finding>(StringComparer.Ordinal);
        foreach (var sarifFile in sarifFiles)
        {
            ReadSarif(repositoryRoot, sarifFile, classifications, findingsByKey);
        }

        var findings = findingsByKey.Values
            .OrderBy(finding => finding.Scope, StringComparer.Ordinal)
            .ThenBy(finding => finding.Classification, StringComparer.Ordinal)
            .ThenBy(finding => finding.Project, StringComparer.Ordinal)
            .ThenBy(finding => finding.File, StringComparer.Ordinal)
            .ThenBy(finding => finding.Line)
            .ThenBy(finding => finding.Column)
            .ToArray();
        var classificationCounts = ClassificationOrder.ToDictionary(
            classification => classification,
            classification => findings.Count(finding => finding.Classification == classification),
            StringComparer.Ordinal);
        var summary = new Ca1506Summary(
            findings.Length,
            findings.Count(finding => finding.Scope == "production"),
            findings.Count(finding => finding.Scope == "test"),
            classificationCounts);
        return new Ca1506Report(1, "CA1506", gitState.Commit, gitState.DirtyWorktree, summary, findings);
    }

    private static Dictionary<string, ProductionClassification> ReadClassifications(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schemaVersion) || schemaVersion.GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported CA1506 classification schema version.");
        }
        if (!root.TryGetProperty("production", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("CA1506 classifications must contain a production array.");
        }

        var classifications = new Dictionary<string, ProductionClassification>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var file = GetRequiredString(entry, "file");
            var classification = GetRequiredString(entry, "classification");
            var rationale = GetRequiredString(entry, "rationale");
            if (!ClassificationOrder.Contains(classification, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Unknown CA1506 classification: {classification}");
            }
            if (!classifications.TryAdd(file, new ProductionClassification(file, classification, rationale)))
            {
                throw new InvalidDataException($"Duplicate CA1506 production classification: {file}");
            }
        }

        return classifications;
    }

    private static void ReadSarif(
        string repositoryRoot,
        string sarifFile,
        Dictionary<string, ProductionClassification> classifications,
        Dictionary<string, Ca1506Finding> findingsByKey)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(sarifFile));
        if (!document.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"SARIF report has no runs array: {Path.GetFileName(sarifFile)}");
        }

        foreach (var run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var results))
            {
                continue;
            }
            if (results.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"SARIF results are malformed: {Path.GetFileName(sarifFile)}");
            }

            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("ruleId", out var ruleId) || ruleId.GetString() != "CA1506")
                {
                    continue;
                }

                var source = SarifSourceLocation.Read(repositoryRoot, result);
                var project = Path.GetFileNameWithoutExtension(sarifFile);
                var key = string.Join('|', "CA1506", project, source.File, source.Line, source.Column);
                if (findingsByKey.ContainsKey(key))
                {
                    continue;
                }

                var review = Classify(source.File, classifications);
                findingsByKey.Add(
                    key,
                    new Ca1506Finding(
                        "CA1506",
                        review.Scope,
                        review.Classification,
                        project,
                        source.File,
                        source.Line,
                        source.Column,
                        ReadMessage(result),
                        review.Rationale));
            }
        }
    }

    private static ClassificationDecision Classify(
        string file,
        Dictionary<string, ProductionClassification> classifications)
    {
        if (file.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
        {
            return new ClassificationDecision(
                "test",
                "test integration",
                "The finding is in integration or behavioral test composition rather than production runtime code.");
        }

        if (classifications.TryGetValue(file, out var classification))
        {
            return new ClassificationDecision("production", classification.Classification, classification.Rationale);
        }

        return new ClassificationDecision(
            "production",
            "needs manual review",
            "This production finding has not yet received a behavior-led architecture classification.");
    }

    private static string ReadMessage(JsonElement result)
    {
        if (!result.TryGetProperty("message", out var message))
        {
            throw new InvalidDataException("CA1506 result has no diagnostic message.");
        }

        var text = message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : message.TryGetProperty("text", out var messageText)
                ? messageText.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("CA1506 result has no diagnostic message text.");
        }

        return text;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidDataException($"CA1506 classification is missing {propertyName}.");
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"CA1506 classification has an empty {propertyName}.");
        }

        return value;
    }

    private sealed record ClassificationDecision(string Scope, string Classification, string Rationale);
}
