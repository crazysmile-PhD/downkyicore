using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

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
    private readonly HashSet<int> knownProcessIds;
    private readonly Lock processIdsLock = new();
    private readonly string instanceName = $"DownKyiH4-{Guid.NewGuid():N}";
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"downkyi-h4-{Guid.NewGuid():N}");
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private string startStatus = "not-started";
    private bool started;
    private int stopped;

    private WindowsEtwResourceFlightRecorder(string targetDirectory, IEnumerable<int> knownProcessIds)
    {
        this.targetDirectory = Path.GetFullPath(targetDirectory);
        this.knownProcessIds = [.. knownProcessIds.Where(processId => processId > 0)];
        StartRecording();
    }

    internal static WindowsEtwResourceFlightRecorder Start(
        string targetDirectory,
        params int[] knownProcessIds) =>
        new(targetDirectory, knownProcessIds);

    internal void AddKnownProcessId(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        lock (processIdsLock)
        {
            knownProcessIds.Add(processId);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A diagnostic recorder failure must be returned as evidence without replacing the test result.")]
    internal string StopAndFormat(bool preserve)
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
                "-lr",
                "-rts");
            if (convert.ExitCode != 0 || !File.Exists(xmlPath))
            {
                return $"etwRecorder=convert-failed exitCode={convert.ExitCode} output={convert.Output}";
            }

            var filtered = FilterTargetEvents(xmlPath);
            var artifactPath = WriteFilteredArtifact(filtered);
            return $"etwRecorder=recorded startedUtc={startedUtc:O} target={targetDirectory} " +
                $"artifact={artifactPath}{Environment.NewLine}{filtered}";
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
        _ = StopAndFormat(preserve: false);
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

    private string FilterTargetEvents(string xmlPath)
    {
        var document = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
        var events = document.Descendants()
            .Where(element => element.Name.LocalName == "Event")
            .ToList();
        var targetEvents = events
            .Where(element => Contains(element, targetDirectory))
            .ToList();
        var identities = targetEvents
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

        var selected = events.Where(
            element =>
            {
                var serialized = element.ToString(SaveOptions.DisableFormatting);
                return serialized.Contains(targetDirectory, StringComparison.OrdinalIgnoreCase) ||
                    identities.Any(identity => serialized.Contains(identity, StringComparison.OrdinalIgnoreCase)) ||
                    (IsProcessLifecycleEvent(serialized) &&
                     processIds.Any(processId => EventReferencesProcess(element, processId)));
            }).ToList();

        var builder = new StringBuilder();
        builder.Append("etlEvents=").Append(events.Count)
            .Append(" directTargetEvents=").Append(targetEvents.Count)
            .Append(" resourceIdentities=").Append(identities.Count)
            .Append(" selectedEvents=").Append(selected.Count);
        foreach (var selectedEvent in selected)
        {
            builder.AppendLine().Append(selectedEvent.ToString(SaveOptions.DisableFormatting));
        }

        return builder.ToString();
    }

    private static string WriteFilteredArtifact(string filtered)
    {
        var artifactDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "test-flight-recorder");
        Directory.CreateDirectory(artifactDirectory);
        var artifactPath = Path.Combine(
            artifactDirectory,
            $"h4-resource-{Guid.NewGuid():N}.log");
        File.WriteAllText(artifactPath, filtered, Encoding.UTF8);
        return artifactPath;
    }

    private static bool Contains(XElement element, string value) =>
        element.ToString(SaveOptions.DisableFormatting)
            .Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool IsResourceIdentity(string? name) =>
        name is not null &&
        (name.Contains("FileObject", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("FileKey", StringComparison.OrdinalIgnoreCase));

    private static bool EventReferencesProcess(XElement element, int processId)
    {
        var value = processId.ToString(CultureInfo.InvariantCulture);
        return element.Descendants().Any(
            descendant =>
                (descendant.Name.LocalName == "Execution" || descendant.Name.LocalName == "Data") &&
                (descendant.Value == value || descendant.Attributes().Any(attribute => attribute.Value == value)));
    }

    private static bool IsProcessLifecycleEvent(string serialized) =>
        serialized.Contains("Process", StringComparison.OrdinalIgnoreCase) &&
        (serialized.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
         serialized.Contains("Stop", StringComparison.OrdinalIgnoreCase) ||
         serialized.Contains("End", StringComparison.OrdinalIgnoreCase));

    private static void AddProcessId(string value, HashSet<int> processIds)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId))
        {
            processIds.Add(processId);
        }
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

        return new ToolResult(process.ExitCode, output.ReplaceLineEndings(" ").Trim());
    }

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
}
