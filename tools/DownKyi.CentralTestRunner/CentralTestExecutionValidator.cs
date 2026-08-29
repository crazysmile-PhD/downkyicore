using System.Globalization;
using System.Xml.Linq;

#pragma warning disable CA1515 // PowerShell compatibility wrappers invoke this compiled semantic owner.

namespace DownKyi.CentralTestRunner;

public static class CentralTestExecutionValidator
{
    public static CentralTestExecutionReport ValidateReport(
        string trxPath,
        IEnumerable<string>? expectedClassNames = null,
        bool requireUniqueReport = false)
    {
        return ValidateReportCore(
            trxPath,
            expectedClassNames,
            requireUniqueReport,
            CentralTestValidatorMutation.None);
    }

    public static CentralTestExecutionReport ValidateExpectedExecution(
        int runnerExitCode,
        string trxPath,
        IEnumerable<string> expectedClassNames)
    {
        return ValidateExpectedExecutionCore(
            runnerExitCode,
            trxPath,
            expectedClassNames,
            CentralTestValidatorMutation.None);
    }

    internal static CentralTestExecutionReport ValidateExpectedExecutionForTesting(
        int runnerExitCode,
        string trxPath,
        IEnumerable<string> expectedClassNames,
        CentralTestValidatorMutation mutation)
    {
        return ValidateExpectedExecutionCore(
            runnerExitCode,
            trxPath,
            expectedClassNames,
            mutation);
    }

    private static CentralTestExecutionReport ValidateExpectedExecutionCore(
        int runnerExitCode,
        string trxPath,
        IEnumerable<string> expectedClassNames,
        CentralTestValidatorMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(expectedClassNames);
        var expected = expectedClassNames.Distinct(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0)
        {
            throw new InvalidOperationException("At least one expected test class is required.");
        }
        if (runnerExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The test runner failed with exit code {runnerExitCode}.");
        }

        var report = ValidateReportCore(
            trxPath,
            expected,
            requireUniqueReport: true,
            mutation);
        if (report.Failed > 0 &&
            mutation != CentralTestValidatorMutation.TreatProcessExitZeroAsPass)
        {
            throw new InvalidOperationException(
                "A successful runner report cannot contain failed test results.");
        }
        if (report.PassedExpectedClasses != expected.Length)
        {
            throw new InvalidOperationException(
                "Every expected test class must contain at least one passed result.");
        }

        return report;
    }

    internal static CentralTestExecutionReport ValidateReportForTesting(
        string trxPath,
        IEnumerable<string>? expectedClassNames,
        bool requireUniqueReport,
        CentralTestValidatorMutation mutation)
    {
        return ValidateReportCore(
            trxPath,
            expectedClassNames,
            requireUniqueReport,
            mutation);
    }

    private static CentralTestExecutionReport ValidateReportCore(
        string trxPath,
        IEnumerable<string>? expectedClassNames,
        bool requireUniqueReport,
        CentralTestValidatorMutation mutation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trxPath);
        var expected = (expectedClassNames ?? []).ToArray();
        if (expected.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Expected test class names cannot be empty.");
        }
        if (!File.Exists(trxPath))
        {
            throw new FileNotFoundException("The expected test report is missing.", trxPath);
        }

        var reportPath = Path.GetFullPath(trxPath);
        var reportDirectory = Path.GetDirectoryName(reportPath)
            ?? throw new InvalidOperationException("The test report directory is unavailable.");
        if (requireUniqueReport)
        {
            var reports = Directory.GetFiles(reportDirectory, "*.trx", SearchOption.TopDirectoryOnly);
            if (reports.Length != 1 ||
                !string.Equals(
                    Path.GetFullPath(reports[0]),
                    reportPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The test result directory must contain exactly the expected TRX report.");
            }
        }

        XDocument trx;
        try
        {
            trx = XDocument.Load(reportPath, LoadOptions.None);
        }
        catch (Exception failure) when (failure is IOException or System.Xml.XmlException)
        {
            throw new InvalidDataException(
                $"The expected test report is malformed: {reportPath}",
                failure);
        }

        var counters = trx.Descendants().SingleOrDefault(node => node.Name.LocalName == "Counters");
        var results = trx.Descendants().Where(node => node.Name.LocalName == "UnitTestResult").ToArray();
        var definitions = trx.Descendants().Where(node => node.Name.LocalName == "UnitTest").ToArray();
        if (counters == null || results.Length == 0 || definitions.Length == 0)
        {
            throw new InvalidDataException(
                "The expected test report has an incomplete result structure.");
        }

        var total = ReadCounter(counters, "total");
        var executed = ReadCounter(counters, "executed");
        var passed = ReadCounter(counters, "passed");
        var failed = ReadCounter(counters, "failed");
        if (executed < 1 && mutation != CentralTestValidatorMutation.AcceptZeroExecuted)
        {
            throw new InvalidDataException("The expected test selection executed no tests.");
        }
        if (executed > total || passed + failed > executed)
        {
            throw new InvalidDataException(
                "The expected test report has inconsistent execution counters.");
        }

        var definitionsById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var testId = definition.Attribute("id")?.Value;
            var method = definition.Elements().SingleOrDefault(node => node.Name.LocalName == "TestMethod");
            var className = method?.Attribute("className")?.Value;
            if (string.IsNullOrWhiteSpace(testId) ||
                string.IsNullOrWhiteSpace(className) ||
                !definitionsById.TryAdd(testId, className))
            {
                throw new InvalidDataException(
                    "The expected test report contains an invalid or ambiguous test definition.");
            }
        }

        foreach (var result in results)
        {
            var testId = result.Attribute("testId")?.Value;
            var outcome = result.Attribute("outcome")?.Value;
            if (string.IsNullOrWhiteSpace(testId) ||
                !definitionsById.ContainsKey(testId) ||
                outcome is not ("Passed" or "Failed" or "NotExecuted"))
            {
                throw new InvalidDataException(
                    "The expected test report contains an invalid execution result.");
            }
        }

        var passedResults = results.Count(ResultPassed);
        var failedResults = results.Count(ResultFailed);
        var executedResults = results.Count(result => !ResultNotExecuted(result));
        if (results.Length != total ||
            executedResults != executed ||
            passedResults != passed ||
            failedResults != failed)
        {
            throw new InvalidDataException(
                "The expected test report counters do not match its execution results.");
        }

        var expectedResults = new Dictionary<string, XElement[]>(StringComparer.Ordinal);
        foreach (var expectedClass in expected.Distinct(StringComparer.Ordinal))
        {
            var classResults = results.Where(result =>
            {
                var id = result.Attribute("testId")?.Value;
                return id != null &&
                       definitionsById.TryGetValue(id, out var actualClass) &&
                       string.Equals(actualClass, expectedClass, StringComparison.Ordinal) &&
                       !ResultNotExecuted(result);
            }).ToArray();
            if (classResults.Length == 0)
            {
                throw new InvalidDataException(
                    $"The report contains no executed result for expected test class '{expectedClass}'.");
            }

            expectedResults.Add(expectedClass, classResults);
        }

        var executedExpected = expectedResults.Values.Sum(items => items.Length);
        var passedExpected = expectedResults.Values.Sum(items => items.Count(ResultPassed));
        var passedExpectedClasses = expectedResults.Values.Count(items => items.Any(ResultPassed));
        return new CentralTestExecutionReport(
            executed,
            executedExpected,
            expectedResults.Count,
            passedExpected,
            passedExpectedClasses,
            failed,
            reportPath);
    }

    private static int ReadCounter(XElement counters, string name)
    {
        var value = counters.Attribute(name)?.Value;
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result) || result < 0)
        {
            throw new InvalidDataException(
                "The expected test report has invalid execution counters.");
        }

        return result;
    }

    private static bool ResultPassed(XElement result) =>
        string.Equals(result.Attribute("outcome")?.Value, "Passed", StringComparison.Ordinal);

    private static bool ResultFailed(XElement result) =>
        string.Equals(result.Attribute("outcome")?.Value, "Failed", StringComparison.Ordinal);

    private static bool ResultNotExecuted(XElement result) =>
        string.Equals(result.Attribute("outcome")?.Value, "NotExecuted", StringComparison.Ordinal);
}
