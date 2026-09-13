using System.Xml.Linq;

namespace DownKyi.CentralTestRunner;

internal static class TrxResultStore
{
    internal static (string ResultsDirectory, string TrxName, string TrxPath) ResolveOutput(
        string repositoryRoot,
        string? requestedResultsDirectory,
        string? requestedTrxName,
        string relativeProject)
    {
        var resultsDirectory = string.IsNullOrWhiteSpace(requestedResultsDirectory)
            ? Path.Combine(repositoryRoot, "artifacts", "test-results")
            : Path.GetFullPath(requestedResultsDirectory, repositoryRoot);
        var trxName = string.IsNullOrWhiteSpace(requestedTrxName)
            ? $"{Path.GetFileNameWithoutExtension(relativeProject)}.trx"
            : ValidateTrxName(requestedTrxName);
        return (resultsDirectory, trxName, Path.Combine(resultsDirectory, trxName));
    }

    internal static void ClearStale(
        (string ResultsDirectory, string TrxName, string TrxPath) trxOutput)
    {
        Directory.CreateDirectory(trxOutput.ResultsDirectory);
        File.Delete(trxOutput.TrxPath);
    }

    internal static void Validate(string trxPath, string trxIdentity)
    {
        var file = new FileInfo(trxPath);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidDataException($"The requested TRX is missing or empty: {trxIdentity}");
        }

        var document = XDocument.Load(trxPath);
        if (!string.Equals(document.Root?.Name.LocalName, "TestRun", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The requested TRX has an unexpected root element: {trxIdentity}");
        }

        var counters = document
            .Descendants()
            .FirstOrDefault(element => string.Equals(
                element.Name.LocalName,
                "Counters",
                StringComparison.Ordinal));
        if (counters is null ||
            !int.TryParse(counters.Attribute("executed")?.Value, out var executed) ||
            !int.TryParse(counters.Attribute("failed")?.Value, out var failed) ||
            executed < 1 ||
            failed != 0)
        {
            throw new InvalidDataException(
                $"The requested TRX does not prove a non-empty passing test run: {trxIdentity}");
        }
    }

    private static string ValidateTrxName(string trxName)
    {
        if (Path.IsPathRooted(trxName) ||
            !string.Equals(Path.GetFileName(trxName), trxName, StringComparison.Ordinal) ||
            trxName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The TRX name must be a file name without directory components.", nameof(trxName));
        }

        return trxName;
    }
}
