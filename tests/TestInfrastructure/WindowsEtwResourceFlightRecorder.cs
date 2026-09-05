using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DownKyi.TestInfrastructure;

internal sealed class WindowsEtwResourceFlightRecorder : IDisposable
{
    private const string ProfileXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <WindowsPerformanceRecorder Version="1.0">
          <Profiles>
            <SystemCollector Id="DownKyiResourceCollector" Name="NT Kernel Logger">
              <BufferSize Value="128" />
              <Buffers Value="64" />
            </SystemCollector>
            <SystemProvider Id="DownKyiResourceProvider">
              <Keywords>
                <Keyword Value="ProcessThread" />
                <Keyword Value="FileIO" />
                <Keyword Value="FileIOInit" />
              </Keywords>
            </SystemProvider>
            <Profile Id="DownKyiResource.Light.File" Name="DownKyiResource" Description="Targeted resource lifecycle" LoggingMode="File" DetailLevel="Light">
              <Collectors>
                <SystemCollectorId Value="DownKyiResourceCollector">
                  <SystemProviderId Value="DownKyiResourceProvider" />
                </SystemCollectorId>
              </Collectors>
            </Profile>
            <Profile Id="DownKyiResource.Light.Memory" Name="DownKyiResource" Description="Targeted resource lifecycle" LoggingMode="Memory" DetailLevel="Light">
              <Collectors>
                <SystemCollectorId Value="DownKyiResourceCollector">
                  <SystemProviderId Value="DownKyiResourceProvider" />
                </SystemCollectorId>
              </Collectors>
            </Profile>
          </Profiles>
        </WindowsPerformanceRecorder>
        """;

    private readonly string targetDirectory;
    private readonly string targetPathSuffix;
    private readonly string testIdentity;
    private readonly HashSet<int> knownProcessIds;
    private readonly Dictionary<int, string> processClassifications = [];
    private readonly Lock processIdsLock = new();
    private readonly string instanceName = $"DownKyiH4-{Guid.NewGuid():N}";
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"downkyi-h4-{Guid.NewGuid():N}");
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private string startStatus = "not-started";
    private bool started;
    private int stopped;

    internal string? ArtifactPath { get; private set; }

    private WindowsEtwResourceFlightRecorder(
        string targetDirectory,
        string testIdentity,
        IEnumerable<int> knownProcessIds)
    {
        this.targetDirectory = Path.GetFullPath(targetDirectory);
        this.testIdentity = testIdentity;
        var pathRoot = Path.GetPathRoot(this.targetDirectory) ?? string.Empty;
        targetPathSuffix = this.targetDirectory[pathRoot.Length..];
        this.knownProcessIds = [.. knownProcessIds.Where(processId => processId > 0)];
        foreach (var processId in this.knownProcessIds)
        {
            processClassifications[processId] = "testhost";
        }

        StartRecording();
    }

    internal static WindowsEtwResourceFlightRecorder Start(
        string targetDirectory,
        string testIdentity,
        params int[] knownProcessIds) =>
        new(targetDirectory, testIdentity, knownProcessIds);

    internal void AddKnownProcessId(int processId, string classification)
    {
        if (processId <= 0)
        {
            return;
        }

        lock (processIdsLock)
        {
            knownProcessIds.Add(processId);
            processClassifications[processId] = classification;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A diagnostic recorder failure must be returned as evidence without replacing the test result.")]
    internal string StopAndFormat(
        bool preserve,
        string probeEvidence,
        string rootCauseStatus)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
        {
            return "etwRecorder=already-stopped";
        }

        if (!started)
        {
            DeleteTemporaryDirectory();
            return $"etwRecorder=unavailable startStatus={startStatus}";
        }

        if (!preserve)
        {
            try
            {
                var cancel = RunTool(
                    "wpr.exe",
                    "-cancel",
                    "-instancename",
                    instanceName);
                return $"etwRecorder=discarded-no-anomaly cancelExitCode={cancel.ExitCode}";
            }
            catch (Exception exception)
            {
                return $"etwRecorder=cancel-failed type={exception.GetType().FullName} message={exception.Message}";
            }
            finally
            {
                DeleteTemporaryDirectory();
            }
        }

        var etlPath = Path.Combine(temporaryDirectory, "resource.etl");
        var xmlPath = Path.Combine(temporaryDirectory, "resource.xml");
        try
        {
            var stop = RunTool(
                "wpr.exe",
                "-stop",
                etlPath,
                "DownKyi H4 targeted resource lifecycle",
                "-skipPdbGen",
                "-instancename",
                instanceName);
            if (stop.ExitCode != 0)
            {
                return $"etwRecorder=stop-failed exitCode={stop.ExitCode} output={stop.Output}";
            }

            var convert = RunTool(
                "tracerpt.exe",
                etlPath,
                "-o",
                xmlPath,
                "-of",
                "XML",
                "-y",
                "-lr");
            if (convert.ExitCode != 0 || !File.Exists(xmlPath))
            {
                return $"etwRecorder=convert-failed exitCode={convert.ExitCode} output={convert.Output}";
            }

            var filtered = FilterTargetEvents(xmlPath);
            ArtifactPath = WriteFilteredArtifact(
                filtered,
                probeEvidence,
                rootCauseStatus);
            return $"etwRecorder=recorded startedUtc={startedUtc:O} target={targetDirectory} " +
                $"{filtered.Summary} artifact={ArtifactPath}";
        }
        catch (Exception exception)
        {
            return $"etwRecorder=failure type={exception.GetType().FullName} message={exception.Message}";
        }
        finally
        {
            DeleteTemporaryDirectory();
        }
    }

    public void Dispose()
    {
        _ = StopAndFormat(
            preserve: false,
            probeEvidence: "probe=disposed-before-report",
            rootCauseStatus: "Root cause not proven.");
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Capability failures are evidence and must not prevent the target test from running.")]
    private void StartRecording()
    {
        if (!OperatingSystem.IsWindows())
        {
            startStatus = "non-windows";
            return;
        }

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var profilePath = Path.Combine(temporaryDirectory, "DownKyiResource.wprp");
            File.WriteAllText(profilePath, ProfileXml, Encoding.UTF8);
            var start = RunTool(
                "wpr.exe",
                "-start",
                $"{profilePath}!DownKyiResource",
                "-instancename",
                instanceName);
            startStatus = $"exitCode={start.ExitCode} output={start.Output}";
            started = start.ExitCode == 0;
        }
        catch (Exception exception)
        {
            startStatus = $"{exception.GetType().FullName}: {exception.Message}";
        }
    }

    private FilteredTrace FilterTargetEvents(string xmlPath)
    {
        var document = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
        var events = document.Descendants()
            .Where(element => element.Name.LocalName == "Event")
            .ToList();
        var targetEvents = events
            .Where(ContainsTargetResource)
            .ToList();
        var retainedTargetEvents = SelectRepresentativeTargetEvents(targetEvents);
        var retainedTargetSet = retainedTargetEvents.ToHashSet();
        var identities = retainedTargetEvents
            .SelectMany(element => element.Descendants())
            .Where(element =>
                element.Name.LocalName == "Data" &&
                IsResourceIdentity(element.Attribute("Name")?.Value))
            .Select(element => element.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<int> processIds;
        lock (processIdsLock)
        {
            processIds = new HashSet<int>(knownProcessIds);
        }
        foreach (var targetEvent in targetEvents)
        {
            foreach (var element in targetEvent.Descendants())
            {
                if (element.Name.LocalName == "Execution")
                {
                    foreach (var attribute in element.Attributes().Where(
                                 attribute => attribute.Name.LocalName.Contains(
                                     "ProcessID",
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        AddProcessId(attribute.Value, processIds);
                    }
                }
                else if (element.Name.LocalName == "Data" &&
                         element.Attribute("Name")?.Value.Contains(
                             "ProcessId",
                             StringComparison.OrdinalIgnoreCase) == true)
                {
                    AddProcessId(element.Value, processIds);
                }
            }
        }

        AddAncestorProcessIds(events, processIds);

        var selected = events.Where(
            element =>
            {
                return retainedTargetSet.Contains(element) ||
                    (IsResourceCleanupOrCloseEvent(element) &&
                     EventReferencesResourceIdentity(element, identities)) ||
                    (IsProcessLifecycleEvent(element) &&
                     processIds.Any(processId => EventReferencesProcess(element, processId)));
            }).ToList();

        var builder = new StringBuilder();
        var summary = new StringBuilder()
            .Append("etlEvents=").Append(events.Count)
            .Append(" directTargetEvents=").Append(targetEvents.Count)
            .Append(" retainedTargetEvents=").Append(retainedTargetEvents.Count)
            .Append(" resourceIdentities=").Append(identities.Count)
            .Append(" selectedEvents=").Append(selected.Count)
            .ToString();
        builder.Append(summary);
        foreach (var selectedEvent in selected)
        {
            builder.AppendLine().Append(
                Sanitize(selectedEvent.ToString(SaveOptions.DisableFormatting)));
        }

        return new FilteredTrace(summary, builder.ToString());
    }

    private string WriteFilteredArtifact(
        FilteredTrace filtered,
        string probeEvidence,
        string rootCauseStatus)
    {
        var artifactRoot = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (string.IsNullOrWhiteSpace(artifactRoot))
        {
            artifactRoot = Directory.GetCurrentDirectory();
        }

        var artifactDirectory = Path.Combine(
            artifactRoot,
            "artifacts",
            "test-flight-recorder");
        Directory.CreateDirectory(artifactDirectory);
        var artifactPath = Path.Combine(
            artifactDirectory,
            $"targeted-resource-forensics-{Guid.NewGuid():N}.log");
        HashSet<int> processIds;
        Dictionary<int, string> classifications;
        lock (processIdsLock)
        {
            processIds = new HashSet<int>(knownProcessIds);
            classifications = new Dictionary<int, string>(processClassifications);
        }

        var header = new StringBuilder()
            .AppendLine("schemaVersion=1")
            .Append("runIdentity=").AppendLine(ReadRunIdentity())
            .Append("testIdentity=").AppendLine(Sanitize(testIdentity))
            .Append("targetResource=").AppendLine(Sanitize(targetDirectory))
            .AppendLine("operation=DirectoryDeleteAccess")
            .Append("recorderStartedUtc=").AppendLine(startedUtc.ToString("O", CultureInfo.InvariantCulture))
            .Append("knownProcessIds=").AppendLine(string.Join(',', processIds.Order()))
            .Append("processClassifications=").AppendLine(
                string.Join(
                    ',',
                    classifications.OrderBy(entry => entry.Key).Select(
                        entry => $"{entry.Key}:{Sanitize(entry.Value)}")))
            .Append("rootCauseStatus=").AppendLine(Sanitize(rootCauseStatus))
            .AppendLine("probeTimeline:")
            .AppendLine(Sanitize(probeEvidence))
            .AppendLine("resourceAndProcessLifecycle:")
            .AppendLine(filtered.Content);
        File.WriteAllText(artifactPath, header.ToString(), Encoding.UTF8);
        return artifactPath;
    }

    private bool ContainsTargetResource(XElement element) =>
        element.ToString(SaveOptions.DisableFormatting)
            .Contains(targetPathSuffix, StringComparison.OrdinalIgnoreCase);

    private static List<XElement> SelectRepresentativeTargetEvents(
        IReadOnlyList<XElement> targetEvents)
    {
        var retained = targetEvents
            .Where(element => !IsDeleteProbeOpen(element))
            .ToHashSet();
        foreach (var group in targetEvents
                     .Where(IsDeleteProbeOpen)
                     .GroupBy(BuildSemanticEventKey, StringComparer.Ordinal))
        {
            retained.Add(group.First());
            retained.Add(group.Last());
        }

        return targetEvents.Where(retained.Contains).ToList();
    }

    private static bool IsDeleteProbeOpen(XElement element) =>
        string.Equals(
            ReadDataValue(element, "ShareAccess")?.Trim(),
            "7",
            StringComparison.Ordinal);

    private static string BuildSemanticEventKey(XElement element) =>
        string.Join(
            '|',
            ReadEventName(element),
            ReadRenderedOpcode(element),
            ReadExecutionProcessId(element),
            ReadDataValue(element, "Status")?.Trim(),
            ReadDataValue(element, "NtStatus")?.Trim(),
            ReadDataValue(element, "CreateOptions")?.Trim(),
            ReadDataValue(element, "CreateAttributes")?.Trim());

    private static bool IsResourceCleanupOrCloseEvent(XElement element)
    {
        var opcode = ReadRenderedOpcode(element);
        return string.Equals(opcode, "Cleanup", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(opcode, "Close", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadDataValue(XElement element, string name) =>
        element.Descendants()
            .FirstOrDefault(descendant =>
                descendant.Name.LocalName == "Data" &&
                descendant.Attribute("Name")?.Value.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            ?.Value;

    private static string? ReadEventName(XElement element) =>
        element.Descendants()
            .FirstOrDefault(descendant => descendant.Name.LocalName == "EventName")?.Value;

    private static string? ReadRenderedOpcode(XElement element) =>
        element.Descendants()
            .FirstOrDefault(descendant =>
                descendant.Name.LocalName == "Opcode" &&
                descendant.Parent?.Name.LocalName == "RenderingInfo")?.Value;

    private static string? ReadExecutionProcessId(XElement element) =>
        element.Descendants()
            .FirstOrDefault(descendant => descendant.Name.LocalName == "Execution")
            ?.Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("ProcessID", StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static bool IsResourceIdentity(string? name) =>
        name is not null &&
        (name.Contains("FileObject", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("FileKey", StringComparison.OrdinalIgnoreCase));

    private static bool EventReferencesResourceIdentity(
        XElement element,
        HashSet<string> identities) =>
        element.Descendants().Any(
            descendant =>
                descendant.Name.LocalName == "Data" &&
                IsResourceIdentity(descendant.Attribute("Name")?.Value) &&
                identities.Contains(descendant.Value));

    private static bool EventReferencesProcess(XElement element, int processId)
    {
        return TryReadDataProcessId(element, "ProcessId", out var observedProcessId) &&
            observedProcessId == processId;
    }

    private static bool IsProcessLifecycleEvent(XElement element)
    {
        var eventName = ReadEventName(element);
        var opcode = ReadRenderedOpcode(element);
        return eventName?.Equals("Process", StringComparison.OrdinalIgnoreCase) == true &&
            (opcode?.Equals("Start", StringComparison.OrdinalIgnoreCase) == true ||
             opcode?.Equals("End", StringComparison.OrdinalIgnoreCase) == true ||
             opcode?.Equals("DCStart", StringComparison.OrdinalIgnoreCase) == true ||
             opcode?.Equals("DCEnd", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void AddProcessId(string value, HashSet<int> processIds)
    {
        if (TryParseProcessId(value, out var processId))
        {
            processIds.Add(processId);
        }
    }

    private static void AddAncestorProcessIds(IEnumerable<XElement> events, HashSet<int> processIds)
    {
        var processEvents = events.Where(IsProcessLifecycleEvent).ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var processEvent in processEvents)
            {
                if (!TryReadDataProcessId(processEvent, "ProcessId", out var processId) ||
                    !processIds.Contains(processId) ||
                    !TryReadDataProcessId(processEvent, "ParentId", out var parentId) ||
                    parentId <= 0)
                {
                    continue;
                }

                changed |= processIds.Add(parentId);
            }
        }
    }

    private static bool TryReadDataProcessId(XElement element, string name, out int processId)
    {
        processId = 0;
        var value = element.Descendants()
            .FirstOrDefault(descendant =>
                descendant.Name.LocalName == "Data" &&
                descendant.Attribute("Name")?.Value.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            ?.Value;
        return value is not null && TryParseProcessId(value, out processId);
    }

    private static bool TryParseProcessId(string value, out int processId)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                trimmed.AsSpan(2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out processId);
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId);
    }

    private static ToolResult RunTool(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Unable to start {executable}.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{executable} did not finish within the diagnostic timeout.");
        }

        return new ToolResult(
            process.ExitCode,
            Sanitize(output.ReplaceLineEndings(" ").Trim()));
    }

    private static string ReadRunIdentity()
    {
        var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local";
        var attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "local";
        var job = Environment.GetEnvironmentVariable("GITHUB_JOB") ?? "local";
        return $"run={runId} attempt={attempt} job={job}";
    }

    private static string Sanitize(string value)
    {
        var sanitized = value;
        sanitized = ReplaceRoot(sanitized, Environment.GetEnvironmentVariable("GITHUB_WORKSPACE"), "<workspace>");
        sanitized = ReplaceRoot(sanitized, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user>");
        sanitized = ReplaceRoot(sanitized, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "<temp>");
        sanitized = Regex.Replace(
            sanitized,
            "(?i)(authorization\\s*[:=]\\s*(?:bearer\\s+)?)[^\\s<]+",
            "$1<redacted>",
            RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(
            sanitized,
            "(?i)(access[_-]?token|cookie|sessdata|bili_jct)(=|%3D)[^&\\s<]+",
            "$1$2<redacted>",
            RegexOptions.CultureInvariant);
        return sanitized;
    }

    private static string ReplaceRoot(string value, string? root, string replacement) =>
        string.IsNullOrWhiteSpace(root)
            ? value
            : value.Replace(root, replacement, StringComparison.OrdinalIgnoreCase);

    private void DeleteTemporaryDirectory()
    {
        try
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ToolResult(int ExitCode, string Output);

    private sealed record FilteredTrace(string Summary, string Content);
}
