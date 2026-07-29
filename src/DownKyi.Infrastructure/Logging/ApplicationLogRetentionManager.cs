namespace DownKyi.Infrastructure.Logging;

internal sealed class ApplicationLogRetentionManager
{
    internal const string DiagnosticDirectoryName = "Diagnostics";
    internal const string EventFilePattern = "events*.jsonl";

    private readonly ApplicationLogOptions _options;
    private readonly object _gate = new();
    private readonly HashSet<string> _activeDiagnosticDirectories;
    private long _ageDeletionCount;
    private long _capacityDeletionCount;
    private long _maintenanceFailureCount;
    private long _retainedBytes;
    private long _lastMaintenanceUtcTicks;
    private long _malformedExportRecords;

    public ApplicationLogRetentionManager(ApplicationLogOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _activeDiagnosticDirectories = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public string ReserveDiagnosticDirectory(DateTimeOffset timestamp)
    {
        var diagnosticsRoot = Path.Combine(_options.LogDirectory, DiagnosticDirectoryName);
        Directory.CreateDirectory(diagnosticsRoot);
        var stem = $"diagnostic-{timestamp:yyyyMMdd'T'HHmmssfff'Z'}";
        lock (_gate)
        {
            for (var index = 0; index < 1000; index++)
            {
                var name = index == 0 ? stem : $"{stem}-{index:D3}";
                var candidate = Path.GetFullPath(Path.Combine(diagnosticsRoot, name));
                if (Directory.Exists(candidate))
                {
                    continue;
                }

                Directory.CreateDirectory(candidate);
                _activeDiagnosticDirectories.Add(candidate);
                return candidate;
            }
        }

        throw new IOException("Unable to reserve a unique diagnostics export directory.");
    }

    public void ReleaseDiagnosticDirectory(string path, bool delete)
    {
        lock (_gate)
        {
            _activeDiagnosticDirectories.Remove(Path.GetFullPath(path));
            if (delete)
            {
                TryDeleteDirectory(path);
            }
        }
    }

    public void Apply(string? activePath, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var threshold = nowUtc.UtcDateTime - _options.MaxRetainedAge;
            var units = GetRetentionUnits();
            foreach (var unit in units.Where(unit => unit.LastWriteTimeUtc < threshold).ToArray())
            {
                if (IsActive(unit, activePath) || !TryDelete(unit))
                {
                    continue;
                }

                units.Remove(unit);
                Interlocked.Increment(ref _ageDeletionCount);
            }

            var retainedBytes = units.Sum(static unit => unit.Length);
            foreach (var unit in units.ToArray())
            {
                if (retainedBytes <= _options.MaxTotalBytes)
                {
                    break;
                }

                if (IsActive(unit, activePath) || !TryDelete(unit))
                {
                    continue;
                }

                retainedBytes -= unit.Length;
                units.Remove(unit);
                Interlocked.Increment(ref _capacityDeletionCount);
            }

            RemoveEmptyDayDirectories();
            Interlocked.Exchange(ref _retainedBytes, Math.Max(0, retainedBytes));
            Interlocked.Exchange(ref _lastMaintenanceUtcTicks, nowUtc.UtcTicks);
        }
    }

    public void RecordMalformedExport()
    {
        Interlocked.Increment(ref _malformedExportRecords);
    }

    public ApplicationLogRetentionMetrics GetMetrics()
    {
        var ticks = Interlocked.Read(ref _lastMaintenanceUtcTicks);
        return new ApplicationLogRetentionMetrics(
            Interlocked.Read(ref _ageDeletionCount),
            Interlocked.Read(ref _capacityDeletionCount),
            Interlocked.Read(ref _maintenanceFailureCount),
            Interlocked.Read(ref _retainedBytes),
            ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero),
            Interlocked.Read(ref _malformedExportRecords));
    }

    private List<RetentionUnit> GetRetentionUnits()
    {
        var units = new List<RetentionUnit>();
        if (!Directory.Exists(_options.LogDirectory))
        {
            return units;
        }

        try
        {
            foreach (var dayDirectory in Directory.GetDirectories(
                         _options.LogDirectory,
                         "????-??-??",
                         SearchOption.TopDirectoryOnly))
            {
                units.AddRange(Directory.GetFiles(
                        dayDirectory,
                        EventFilePattern,
                        SearchOption.TopDirectoryOnly)
                    .Select(static path => new FileInfo(path))
                    .Select(static file => new RetentionUnit(
                        file.FullName,
                        IsDirectory: false,
                        file.LastWriteTimeUtc,
                        file.Length)));
            }

            var diagnosticsRoot = Path.Combine(_options.LogDirectory, DiagnosticDirectoryName);
            if (Directory.Exists(diagnosticsRoot))
            {
                foreach (var directory in Directory.GetDirectories(
                             diagnosticsRoot,
                             "diagnostic-*",
                             SearchOption.TopDirectoryOnly))
                {
                    var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                        .Select(static path => new FileInfo(path))
                        .ToArray();
                    units.Add(new RetentionUnit(
                        Path.GetFullPath(directory),
                        IsDirectory: true,
                        files.Length == 0
                            ? new DirectoryInfo(directory).LastWriteTimeUtc
                            : files.Max(static file => file.LastWriteTimeUtc),
                        files.Sum(static file => file.Length)));
                }
            }
        }
        catch (IOException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
        }

        return units.OrderBy(static unit => unit.LastWriteTimeUtc).ToList();
    }

    private bool IsActive(RetentionUnit unit, string? activePath)
    {
        return unit.IsDirectory
            ? _activeDiagnosticDirectories.Contains(unit.Path)
            : activePath != null && PathsEqual(unit.Path, activePath);
    }

    private bool TryDelete(RetentionUnit unit)
    {
        if (unit.IsDirectory)
        {
            return TryDeleteDirectory(unit.Path);
        }

        try
        {
            File.Delete(unit.Path);
            return true;
        }
        catch (IOException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
            return false;
        }
    }

    private bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
            return false;
        }
    }

    private void RemoveEmptyDayDirectories()
    {
        if (!Directory.Exists(_options.LogDirectory))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(
                     _options.LogDirectory,
                     "????-??-??",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
                Interlocked.Increment(ref _maintenanceFailureCount);
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _maintenanceFailureCount);
            }
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed record RetentionUnit(
        string Path,
        bool IsDirectory,
        DateTime LastWriteTimeUtc,
        long Length);
}

internal sealed record ApplicationLogRetentionMetrics(
    long AgeDeletionCount,
    long CapacityDeletionCount,
    long MaintenanceFailureCount,
    long RetainedBytes,
    DateTimeOffset? LastMaintenanceUtc,
    long MalformedExportRecords);
