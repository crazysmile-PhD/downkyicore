#pragma warning disable CA1031
#pragma warning disable CA1859
using System.Diagnostics;
using System.Globalization;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadFilesystemAdversarialMatrixTests
{
    [Fact]
    public async Task JunctionAliasReservationPersistsAcrossReopen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-output-focused",
            Guid.NewGuid().ToString("N"));

        var realRoot = Path.Combine(root, "real");
        var junctionRoot = Path.Combine(root, "junction");

        var report = new List<string>();
        var knownFindings = new List<string>();

        Directory.CreateDirectory(realRoot);

        try
        {
            await CreateJunctionAsync(
                    junctionRoot,
                    realRoot,
                    cancellationToken)
                .ConfigureAwait(true);

            await RunReopenAliasCheckAsync(
                    root,
                    realRoot,
                    junctionRoot,
                    "junction",
                    report,
                    knownFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.True(
                knownFindings.Count == 0,
                "#176 reopen regression reproduced:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, knownFindings));
        }
        finally
        {
            var removed = TryDeleteDirectoryLink(
                junctionRoot,
                expectedToExist: true,
                report);

            if (removed && Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException exception)
                {
                    report.Add(
                        $"CLEANUP OBSERVATION: {exception.GetType().Name}: " +
                        exception.Message);
                }
            }
        }
    }

    [Fact]
    public async Task SymlinkAliasReservationPersistsAcrossReopen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-output-focused",
            Guid.NewGuid().ToString("N"));

        var realRoot = Path.Combine(root, "real");
        var symlinkRoot = Path.Combine(root, "symlink");

        var report = new List<string>();
        var knownFindings = new List<string>();

        Directory.CreateDirectory(realRoot);

        var symlinkCreated = false;

        try
        {
            symlinkCreated =
                TryCreateDirectorySymlink(
                    symlinkRoot,
                    realRoot,
                    report);

            if (!symlinkCreated)
            {
                return;
            }

            await RunReopenAliasCheckAsync(
                    root,
                    realRoot,
                    symlinkRoot,
                    "symlink",
                    report,
                    knownFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.True(
                knownFindings.Count == 0,
                "#176 reopen regression reproduced:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, knownFindings));
        }
        finally
        {
            var removed = TryDeleteDirectoryLink(
                symlinkRoot,
                symlinkCreated,
                report);

            if (removed && Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException exception)
                {
                    report.Add(
                        $"CLEANUP OBSERVATION: {exception.GetType().Name}: " +
                        exception.Message);
                }
            }
        }
    }
    [Fact]
    public async Task SymlinkAliasCannotBypassNoSuffixReservation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-output-focused",
            Guid.NewGuid().ToString("N"));

        var realRoot = Path.Combine(root, "real");
        var symlinkRoot = Path.Combine(root, "symlink");

        var report = new List<string>();
        var knownFindings = new List<string>();

        Directory.CreateDirectory(realRoot);

        var symlinkCreated = false;

        try
        {
            symlinkCreated =
                TryCreateDirectorySymlink(
                    symlinkRoot,
                    realRoot,
                    report);

            if (!symlinkCreated)
            {
                return;
            }

            await RunNoSuffixAliasCheckAsync(
                    root,
                    realRoot,
                    symlinkRoot,
                    "symlink",
                    report,
                    knownFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.True(
                knownFindings.Count == 0,
                "#176 focused regression reproduced:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, knownFindings));
        }
        finally
        {
            var removed = TryDeleteDirectoryLink(
                symlinkRoot,
                symlinkCreated,
                report);

            if (removed && Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException exception)
                {
                    report.Add(
                        $"CLEANUP OBSERVATION: {exception.GetType().Name}: " +
                        exception.Message);
                }
            }
        }
    }
    [Fact]
    public async Task JunctionAliasCannotBypassNoSuffixReservation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-output-focused",
            Guid.NewGuid().ToString("N"));

        var realRoot = Path.Combine(root, "real");
        var junctionRoot = Path.Combine(root, "junction");

        var report = new List<string>();
        var knownFindings = new List<string>();

        Directory.CreateDirectory(realRoot);

        try
        {
            await CreateJunctionAsync(
                    junctionRoot,
                    realRoot,
                    cancellationToken)
                .ConfigureAwait(true);

            await RunNoSuffixAliasCheckAsync(
                    root,
                    realRoot,
                    junctionRoot,
                    "junction",
                    report,
                    knownFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.True(
                knownFindings.Count == 0,
                "#176 focused regression reproduced:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, knownFindings));
        }
        finally
        {
            var removed = TryDeleteDirectoryLink(
                junctionRoot,
                expectedToExist: true,
                report);

            if (removed && Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException exception)
                {
                    report.Add(
                        $"CLEANUP OBSERVATION: {exception.GetType().Name}: " +
                        exception.Message);
                }
            }
        }
    }
    [Fact]
    public async Task UnicodeNfcNfdPhysicalIdentityMustMatchFilesystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-output-focused",
            Guid.NewGuid().ToString("N"));

        var realRoot = Path.Combine(root, "real");

        var report = new List<string>();
        var newFindings = new List<string>();

        Directory.CreateDirectory(realRoot);

        try
        {
            await RunUnicodeNormalizationProbeAsync(
                    root,
                    realRoot,
                    report,
                    newFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.True(
                newFindings.Count == 0,
                "NFC/NFD focused regression reproduced:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, newFindings));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException exception)
                {
                    report.Add(
                        $"CLEANUP OBSERVATION: {exception.GetType().Name}: " +
                        exception.Message);
                }
            }
        }
    }
    [Fact(Explicit = true)]
    public async Task AdmissionSuffixScalingProbe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        var reportPath =
            Environment.GetEnvironmentVariable("DOWNKYI_SCALING_REPORT")
            ?? Path.Combine(
                Path.GetTempPath(),
                $"downkyi-scaling-{Guid.NewGuid():N}.txt");

        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-output-scaling",
            Guid.NewGuid().ToString("N"));

        var report = new List<string>
        {
            "DownKyi admission suffix scaling probe",
            $"UTC: {DateTimeOffset.UtcNow:O}",
            "",
            "N`tElapsedMs`tMsPerAdmission`tRatio"
        };

        Directory.CreateDirectory(root);

        // JIT / SQLite warm-up. Not included in measurements.
        {
            var warmRoot = Path.Combine(root, "warmup");
            var warmRealRoot = Path.Combine(warmRoot, "real");
            var warmDbPath = Path.Combine(warmRoot, "db", "warmup.db");
            var warmBase = Path.Combine(warmRealRoot, "scaling-output");

            Directory.CreateDirectory(warmRealRoot);

            using var warmSession =
                new AdmissionSession(warmDbPath);

            for (var index = 0; index < 32; index++)
            {
                var item =
                    CreateItem(
                        $"warmup-{index:D6}",
                        warmBase);

                await warmSession.AdmitAsync(
                        item,
                        autoAddNumberSuffix: true,
                        cancellationToken)
                    .ConfigureAwait(true);
            }
        }

        var sizes = new[]
        {
            128,
            256,
            512,
            1024,
            2048
        };

        double? previousElapsedMs = null;

        foreach (var size in sizes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sampleRoot =
                Path.Combine(root, $"n-{size}");

            var realRoot =
                Path.Combine(sampleRoot, "real");

            var dbPath =
                Path.Combine(
                    sampleRoot,
                    "db",
                    "scaling.db");

            var outputBase =
                Path.Combine(
                    realRoot,
                    "scaling-output");

            Directory.CreateDirectory(realRoot);

            var items =
                Enumerable.Range(0, size)
                    .Select(
                        index =>
                            CreateItem(
                                $"scaling-{size}-{index:D6}",
                                outputBase))
                    .ToArray();

            using var session =
                new AdmissionSession(dbPath);

            var stopwatch = Stopwatch.StartNew();

            foreach (var item in items)
            {
                await session.AdmitAsync(
                        item,
                        autoAddNumberSuffix: true,
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            stopwatch.Stop();

            var elapsedMs =
                stopwatch.Elapsed.TotalMilliseconds;

            var msPerAdmission =
                elapsedMs / size;

            var ratio =
                previousElapsedMs.HasValue
                    ? elapsedMs / previousElapsedMs.Value
                    : double.NaN;

            report.Add(
                string.Join(
                    "`t",
                    size.ToString(CultureInfo.InvariantCulture),
                    elapsedMs.ToString(
                        "F3",
                        CultureInfo.InvariantCulture),
                    msPerAdmission.ToString(
                        "F6",
                        CultureInfo.InvariantCulture),
                    double.IsNaN(ratio)
                        ? "-"
                        : ratio.ToString(
                            "F3",
                            CultureInfo.InvariantCulture)));

            var persisted =
                await session.GetUnfinishedAsync(
                        cancellationToken)
                    .ConfigureAwait(true);

            Assert.Equal(size, persisted.Count);

            previousElapsedMs = elapsedMs;
        }

        var reportDirectory =
            Path.GetDirectoryName(reportPath);

        if (!string.IsNullOrEmpty(reportDirectory))
        {
            Directory.CreateDirectory(reportDirectory);
        }

        await File.WriteAllLinesAsync(
                reportPath,
                report,
                cancellationToken)
            .ConfigureAwait(true);
    }
    [Fact(Explicit = true)]
    public async Task BroadWindowsFilesystemReservationMatrix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        var iterations = GetIterationCount();

        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-output-adversarial",
            Guid.NewGuid().ToString("N"));

        var realRoot = Path.Combine(root, "real");
        var junctionRoot = Path.Combine(root, "junction");
        var symlinkRoot = Path.Combine(root, "symlink");

        var reportPath =
            Environment.GetEnvironmentVariable("DOWNKYI_ADVERSARIAL_REPORT")
            ?? Path.Combine(
                Path.GetTempPath(),
                $"downkyi-adversarial-{Guid.NewGuid():N}.txt");

        var report = new List<string>();
        var newFindings = new List<string>();
        var knownFindings = new List<string>();

        report.Add("DownKyi Windows filesystem / output reservation adversarial matrix");
        report.Add($"UTC: {DateTimeOffset.UtcNow:O}");
        report.Add($"Iterations per stress matrix: {iterations}");
        report.Add("");

        Directory.CreateDirectory(realRoot);
        Directory.CreateDirectory(Path.Combine(realRoot, "scratch"));

        var symlinkCreated = false;
        var linksRemoved = true;

        try
        {
            await CreateJunctionAsync(
                    junctionRoot,
                    realRoot,
                    cancellationToken)
                .ConfigureAwait(true);

            report.Add("SETUP PASS: Windows junction created.");

            var physicalProbe =
                Path.Combine(realRoot, "physical-identity.probe");

            var junctionProbe =
                Path.Combine(junctionRoot, "physical-identity.probe");

            await File.WriteAllTextAsync(
                    physicalProbe,
                    "physical-identity",
                    cancellationToken)
                .ConfigureAwait(true);

            if (!File.Exists(junctionProbe))
            {
                newFindings.Add(
                    "HARNESS: junction does not resolve to the physical target.");
            }
            else
            {
                report.Add(
                    "SETUP PASS: junction path observes a file written through real path.");
            }

            File.Delete(physicalProbe);

            symlinkCreated =
                TryCreateDirectorySymlink(
                    symlinkRoot,
                    realRoot,
                    report);

            await RunLexicalAliasMatrixAsync(
                    root,
                    realRoot,
                    report,
                    newFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            await RunDiskCollisionAcrossAliasAsync(
                    root,
                    realRoot,
                    junctionRoot,
                    "junction",
                    report,
                    newFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            await RunNoSuffixAliasCheckAsync(
                    root,
                    realRoot,
                    junctionRoot,
                    "junction",
                    report,
                    knownFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            await RunReopenAliasCheckAsync(
                    root,
                    realRoot,
                    junctionRoot,
                    "junction",
                    report,
                    knownFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            if (symlinkCreated)
            {
                await RunDiskCollisionAcrossAliasAsync(
                        root,
                        realRoot,
                        symlinkRoot,
                        "symlink",
                        report,
                        newFindings,
                        cancellationToken)
                    .ConfigureAwait(true);

                await RunNoSuffixAliasCheckAsync(
                        root,
                        realRoot,
                        symlinkRoot,
                        "symlink",
                        report,
                        knownFindings,
                        cancellationToken)
                    .ConfigureAwait(true);

                await RunReopenAliasCheckAsync(
                        root,
                        realRoot,
                        symlinkRoot,
                        "symlink",
                        report,
                        knownFindings,
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            await RunUnicodeNormalizationProbeAsync(
                    root,
                    realRoot,
                    report,
                    newFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            await RunLexicalStressAsync(
                    root,
                    realRoot,
                    iterations,
                    report,
                    newFindings,
                    cancellationToken)
                .ConfigureAwait(true);

            var physicalAliasRoots = new List<(string Name, string Root)>
            {
                ("real", realRoot),
                ("junction", junctionRoot)
            };

            if (symlinkCreated)
            {
                physicalAliasRoots.Add(("symlink", symlinkRoot));
            }

            await RunPhysicalAliasStressAsync(
                    root,
                    realRoot,
                    physicalAliasRoots,
                    iterations,
                    report,
                    newFindings,
                    knownFindings,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            newFindings.Add(
                $"HARNESS/UNEXPECTED EXCEPTION: " +
                $"{exception.GetType().Name}: {exception.Message}");

            report.Add("");
            report.Add("UNEXPECTED EXCEPTION:");
            report.Add(exception.ToString());
        }
        finally
        {
            linksRemoved &=
                TryDeleteDirectoryLink(
                    symlinkRoot,
                    symlinkCreated,
                    report);

            linksRemoved &=
                TryDeleteDirectoryLink(
                    junctionRoot,
                    expectedToExist: true,
                    report);

            if (linksRemoved)
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception exception)
                {
                    report.Add(
                        $"CLEANUP OBSERVATION: {exception.GetType().Name}: " +
                        exception.Message);
                }
            }
            else
            {
                report.Add(
                    "CLEANUP OBSERVATION: skipped recursive root deletion " +
                    "because a reparse-point link could not be removed safely.");
            }

            report.Add("");
            report.Add("==================================================");
            report.Add($"KNOWN findings: {knownFindings.Count}");

            foreach (var finding in knownFindings)
            {
                report.Add($"KNOWN: {finding}");
            }

            report.Add("");
            report.Add($"NEW / candidate findings: {newFindings.Count}");

            foreach (var finding in newFindings)
            {
                report.Add($"NEW: {finding}");
            }

            report.Add("==================================================");

            var reportDirectory = Path.GetDirectoryName(reportPath);

            if (!string.IsNullOrEmpty(reportDirectory))
            {
                Directory.CreateDirectory(reportDirectory);
            }

            await File.WriteAllLinesAsync(
                    reportPath,
                    report,
                    cancellationToken)
                .ConfigureAwait(true);
        }

        Assert.True(
            newFindings.Count == 0,
            $"Adversarial matrix found {newFindings.Count} new/candidate " +
            $"finding(s). Full report: {reportPath}{Environment.NewLine}" +
            string.Join(Environment.NewLine, newFindings.Take(20)));
    }

    private static int GetIterationCount()
    {
        var value =
            Environment.GetEnvironmentVariable(
                "DOWNKYI_ADVERSARIAL_ITERATIONS");

        return int.TryParse(
                   value,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var parsed)
               && parsed >= 128
            ? parsed
            : 4096;
    }

    private static async Task RunLexicalAliasMatrixAsync(
        string root,
        string realRoot,
        ICollection<string> report,
        ICollection<string> newFindings,
        CancellationToken cancellationToken)
    {
        report.Add("");
        report.Add("=== Lexical alias matrix ===");

        var cases = new[]
        {
            "case",
            "dot-segment",
            "forward-slash",
            "relative"
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var name = cases[index];

            var baseName = $"lexical-{index}";
            var canonical = Path.Combine(realRoot, baseName);

            var alias = name switch
            {
                "case" =>
                    Path.Combine(
                        realRoot,
                        baseName.ToUpperInvariant()),

                "dot-segment" =>
                    Path.Combine(
                        realRoot,
                        "scratch",
                        "..",
                        baseName),

                "forward-slash" =>
                    canonical.Replace(
                        '\\',
                        '/'),

                "relative" =>
                    Path.GetRelativePath(
                        Environment.CurrentDirectory,
                        canonical),

                _ => throw new InvalidOperationException(
                    $"Unknown lexical case '{name}'.")
            };

            var dbPath = Path.Combine(
                root,
                "db",
                $"lexical-{index}.db");

            using var session = new AdmissionSession(dbPath);

            var first =
                CreateItem(
                    $"lexical-first-{index}",
                    canonical);

            var second =
                CreateItem(
                    $"lexical-second-{index}",
                    alias);

            await session.AdmitAsync(
                    first,
                    autoAddNumberSuffix: true,
                    cancellationToken)
                .ConfigureAwait(true);

            await session.AdmitAsync(
                    second,
                    autoAddNumberSuffix: true,
                    cancellationToken)
                .ConfigureAwait(true);

            var expectedKey =
                DownloadOutputPathKey.Create(
                    $"{alias}(1)",
                    ignoreCase: true);

            var actualKey =
                DownloadOutputPathKey.Create(
                    second.DownloadBase.FilePath,
                    ignoreCase: true);

            if (!string.Equals(
                    expectedKey,
                    actualKey,
                    StringComparison.Ordinal))
            {
                newFindings.Add(
                    $"Lexical alias '{name}' did not share reservation. " +
                    $"Expected key '{expectedKey}', actual '{actualKey}'.");
            }
            else
            {
                report.Add(
                    $"PASS lexical alias: {name}");
            }
        }
    }

    private static async Task RunDiskCollisionAcrossAliasAsync(
        string root,
        string realRoot,
        string aliasRoot,
        string aliasName,
        ICollection<string> report,
        ICollection<string> newFindings,
        CancellationToken cancellationToken)
    {
        report.Add("");
        report.Add(
            $"=== Existing disk collision through {aliasName} ===");

        var realBase =
            Path.Combine(
                realRoot,
                $"disk-collision-{aliasName}");

        var aliasBase =
            Path.Combine(
                aliasRoot,
                $"disk-collision-{aliasName}");

        var existingFile = $"{realBase}.mp4";

        await File.WriteAllTextAsync(
                existingFile,
                "foreign-output",
                cancellationToken)
            .ConfigureAwait(true);

        try
        {
            var dbPath = Path.Combine(
                root,
                "db",
                $"disk-{aliasName}.db");

            using var session =
                new AdmissionSession(dbPath);

            var item =
                CreateItem(
                    $"disk-{aliasName}",
                    aliasBase);

            await session.AdmitAsync(
                    item,
                    autoAddNumberSuffix: true,
                    cancellationToken)
                .ConfigureAwait(true);

            var expectedKey =
                DownloadOutputPathKey.Create(
                    $"{aliasBase}(1)",
                    ignoreCase: true);

            var actualKey =
                DownloadOutputPathKey.Create(
                    item.DownloadBase.FilePath,
                    ignoreCase: true);

            if (!string.Equals(
                    expectedKey,
                    actualKey,
                    StringComparison.Ordinal))
            {
                newFindings.Add(
                    $"Existing physical file was not detected through " +
                    $"{aliasName}. Expected '{expectedKey}', " +
                    $"actual '{actualKey}'.");
            }
            else
            {
                report.Add(
                    $"PASS disk collision through {aliasName}.");
            }
        }
        finally
        {
            if (File.Exists(existingFile))
            {
                File.Delete(existingFile);
            }
        }
    }

    private static async Task RunNoSuffixAliasCheckAsync(
        string root,
        string realRoot,
        string aliasRoot,
        string aliasName,
        ICollection<string> report,
        ICollection<string> knownFindings,
        CancellationToken cancellationToken)
    {
        report.Add("");
        report.Add(
            $"=== No-suffix fail-closed through {aliasName} ===");

        var realBase =
            Path.Combine(
                realRoot,
                $"no-suffix-{aliasName}");

        var aliasBase =
            Path.Combine(
                aliasRoot,
                $"no-suffix-{aliasName}");

        var dbPath = Path.Combine(
            root,
            "db",
            $"no-suffix-{aliasName}.db");

        using var session =
            new AdmissionSession(dbPath);

        var first =
            CreateItem(
                $"no-suffix-first-{aliasName}",
                realBase);

        await session.AdmitAsync(
                first,
                autoAddNumberSuffix: true,
                cancellationToken)
            .ConfigureAwait(true);

        var second =
            CreateItem(
                $"no-suffix-second-{aliasName}",
                aliasBase);

        var blocked = false;
        Exception? blockingException = null;

        try
        {
            await session.AdmitAsync(
                    second,
                    autoAddNumberSuffix: false,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
            when (exception is IOException
                  or InvalidOperationException)
        {
            blocked = true;
            blockingException = exception;
        }

        if (!blocked)
        {
            knownFindings.Add(
                $"#176 family: {aliasName} alias bypasses fail-closed " +
                "no-suffix reservation.");
        }
        else
        {
            report.Add(
                $"PASS no-suffix {aliasName}: blocked via " +
                $"{blockingException!.GetType().Name}.");
        }
    }

    private static async Task RunReopenAliasCheckAsync(
        string root,
        string realRoot,
        string aliasRoot,
        string aliasName,
        ICollection<string> report,
        ICollection<string> knownFindings,
        CancellationToken cancellationToken)
    {
        report.Add("");
        report.Add(
            $"=== Durable reopen through {aliasName} ===");

        var realBase =
            Path.Combine(
                realRoot,
                $"reopen-{aliasName}");

        var aliasBase =
            Path.Combine(
                aliasRoot,
                $"reopen-{aliasName}");

        var dbPath = Path.Combine(
            root,
            "db",
            $"reopen-{aliasName}.db");

        using (var firstSession =
               new AdmissionSession(dbPath))
        {
            var first =
                CreateItem(
                    $"reopen-first-{aliasName}",
                    realBase);

            await firstSession.AdmitAsync(
                    first,
                    autoAddNumberSuffix: true,
                    cancellationToken)
                .ConfigureAwait(true);
        }

        using var secondSession =
            new AdmissionSession(dbPath);

        var second =
            CreateItem(
                $"reopen-second-{aliasName}",
                aliasBase);

        await secondSession.AdmitAsync(
                second,
                autoAddNumberSuffix: true,
                cancellationToken)
            .ConfigureAwait(true);

        var expectedKey =
            DownloadOutputPathKey.Create(
                $"{aliasBase}(1)",
                ignoreCase: true);

        var actualKey =
            DownloadOutputPathKey.Create(
                second.DownloadBase.FilePath,
                ignoreCase: true);

        if (!string.Equals(
                expectedKey,
                actualKey,
                StringComparison.Ordinal))
        {
            knownFindings.Add(
                $"#176 family persists across SQLite reopen for " +
                $"{aliasName}: '{second.DownloadBase.FilePath}'.");
        }
        else
        {
            report.Add(
                $"PASS reopen reservation through {aliasName}.");
        }
    }

    private static async Task RunUnicodeNormalizationProbeAsync(
        string root,
        string realRoot,
        ICollection<string> report,
        ICollection<string> newFindings,
        CancellationToken cancellationToken)
    {
        report.Add("");
        report.Add("=== Unicode NFC/NFD physical identity probe ===");

        const string composedName = "caf\u00E9-output";
        const string decomposedName = "cafe\u0301-output";

        var composedBase =
            Path.Combine(
                realRoot,
                composedName);

        var decomposedBase =
            Path.Combine(
                realRoot,
                decomposedName);

        var composedProbe =
            $"{composedBase}.probe";

        var decomposedProbe =
            $"{decomposedBase}.probe";

        await File.WriteAllTextAsync(
                composedProbe,
                "unicode-probe",
                cancellationToken)
            .ConfigureAwait(true);

        var fileSystemTreatsThemAsSame =
            File.Exists(decomposedProbe);

        File.Delete(composedProbe);

        if (fileSystemTreatsThemAsSame)
        {
            report.Add(
                "OBSERVATION: this filesystem collapses NFC/NFD; " +
                "false-collision test skipped.");
            return;
        }

        report.Add(
            "SETUP PASS: filesystem treats NFC and NFD names as " +
            "physically distinct paths.");

        var dbPath = Path.Combine(
            root,
            "db",
            "unicode-normalization.db");

        using var session =
            new AdmissionSession(dbPath);

        var first =
            CreateItem(
                "unicode-composed",
                composedBase);

        var second =
            CreateItem(
                "unicode-decomposed",
                decomposedBase);

        await session.AdmitAsync(
                first,
                autoAddNumberSuffix: true,
                cancellationToken)
            .ConfigureAwait(true);

        await session.AdmitAsync(
                second,
                autoAddNumberSuffix: true,
                cancellationToken)
            .ConfigureAwait(true);

        var expected =
            Path.GetFullPath(decomposedBase);

        var actual =
            Path.GetFullPath(
                second.DownloadBase.FilePath);

        if (!string.Equals(
                expected,
                actual,
                StringComparison.OrdinalIgnoreCase))
        {
            newFindings.Add(
                "CANDIDATE: Windows filesystem keeps NFC/NFD output " +
                "names physically distinct, but reservation normalization " +
                $"collapsed them. Expected second path '{expected}', " +
                $"actual '{actual}'.");
        }
        else
        {
            report.Add(
                "PASS Unicode: physically distinct NFC/NFD paths " +
                "remain independently claimable.");
        }
    }

    private static async Task RunLexicalStressAsync(
        string root,
        string realRoot,
        int iterations,
        ICollection<string> report,
        ICollection<string> newFindings,
        CancellationToken cancellationToken)
    {
        report.Add("");
        report.Add(
            $"=== Lexical randomized stress: {iterations} admissions ===");

        var dbPath = Path.Combine(
            root,
            "db",
            "lexical-stress.db");

        var physicalClaims =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        var duplicateCount = 0;
        uint state = 0x4D595DF4;

        AdmissionSession? session = null;

        try
        {
            session =
                new AdmissionSession(dbPath);

            for (var index = 0; index < iterations; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (index > 0 && index % 64 == 0)
                {
                    session.Dispose();

                    session =
                        new AdmissionSession(dbPath);
                }

                state = unchecked(
                    state * 1_664_525U
                    + 1_013_904_223U);

                var canonical =
                    Path.Combine(
                        realRoot,
                        "lexical-stress-output");

                var candidate =
                    (state % 5U) switch
                    {
                        0 => canonical,

                        1 => Path.Combine(
                            realRoot,
                            "LEXICAL-STRESS-OUTPUT"),

                        2 => Path.Combine(
                            realRoot,
                            "scratch",
                            "..",
                            "lexical-stress-output"),

                        3 => canonical.Replace(
                            '\\',
                            '/'),

                        _ => Path.GetRelativePath(
                            Environment.CurrentDirectory,
                            canonical)
                    };

                var item =
                    CreateItem(
                        $"lex-stress-{index:D6}",
                        candidate);

                try
                {
                    await session.AdmitAsync(
                            item,
                            autoAddNumberSuffix: true,
                            cancellationToken)
                        .ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    newFindings.Add(
                        $"Lexical stress admission {index} threw " +
                        $"{exception.GetType().Name}: {exception.Message}");
                    continue;
                }

                var key =
                    DownloadOutputPathKey.Create(
                        item.DownloadBase.FilePath,
                        ignoreCase: true);

                if (physicalClaims.TryGetValue(
                        key,
                        out var previous))
                {
                    duplicateCount++;

                    if (duplicateCount <= 10)
                    {
                        newFindings.Add(
                            $"Lexical stress duplicate reservation key " +
                            $"'{key}' between '{previous}' and " +
                            $"'{item.DownloadBase.Id}'.");
                    }
                }
                else
                {
                    physicalClaims[key] =
                        item.DownloadBase.Id;
                }
            }

            var persisted =
                await session.GetUnfinishedAsync(
                        cancellationToken)
                    .ConfigureAwait(true);

            if (persisted.Count != iterations)
            {
                newFindings.Add(
                    $"Lexical stress persisted count mismatch. " +
                    $"Expected {iterations}, actual {persisted.Count}.");
            }

            if (duplicateCount == 0)
            {
                report.Add(
                    $"PASS lexical stress: {iterations} claims, " +
                    "0 duplicate canonical reservations.");
            }
            else
            {
                report.Add(
                    $"FAIL lexical stress: {duplicateCount} duplicates.");
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static async Task RunPhysicalAliasStressAsync(
        string root,
        string realRoot,
        IReadOnlyList<(string Name, string Root)> aliasRoots,
        int iterations,
        ICollection<string> report,
        ICollection<string> newFindings,
        ICollection<string> knownFindings,
        CancellationToken cancellationToken)
    {
        report.Add("");
        report.Add(
            $"=== Physical alias stress: {iterations} admissions ===");

        report.Add(
            "Alias roots: " +
            string.Join(
                ", ",
                aliasRoots.Select(alias => alias.Name)));

        var dbPath = Path.Combine(
            root,
            "db",
            "physical-alias-stress.db");

        var physicalClaims =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        var duplicatePhysicalClaims = 0;
        var admissionExceptions = 0;

        uint state = 0xA341316C;

        AdmissionSession? session = null;

        try
        {
            session =
                new AdmissionSession(dbPath);

            for (var index = 0; index < iterations; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (index > 0 && index % 64 == 0)
                {
                    session.Dispose();

                    session =
                        new AdmissionSession(dbPath);
                }

                state = unchecked(
                    state * 1_664_525U
                    + 1_013_904_223U);

                var alias =
                    aliasRoots[
                        (int)(state % (uint)aliasRoots.Count)];

                var candidate =
                    Path.Combine(
                        alias.Root,
                        "physical-alias-stress-output");

                var item =
                    CreateItem(
                        $"alias-stress-{index:D6}",
                        candidate);

                try
                {
                    await session.AdmitAsync(
                            item,
                            autoAddNumberSuffix: true,
                            cancellationToken)
                        .ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    admissionExceptions++;

                    if (admissionExceptions <= 10)
                    {
                        newFindings.Add(
                            $"Physical alias stress admission {index} " +
                            $"through {alias.Name} threw " +
                            $"{exception.GetType().Name}: " +
                            exception.Message);
                    }

                    continue;
                }

                var physicalKey =
                    MapToKnownPhysicalTarget(
                        item.DownloadBase.FilePath,
                        realRoot,
                        aliasRoots);

                if (physicalClaims.TryGetValue(
                        physicalKey,
                        out var previous))
                {
                    duplicatePhysicalClaims++;

                    if (duplicatePhysicalClaims <= 10)
                    {
                        report.Add(
                            $"KNOWN duplicate physical claim: " +
                            $"'{physicalKey}' between '{previous}' " +
                            $"and '{item.DownloadBase.Id}' " +
                            $"via {alias.Name}.");
                    }
                }
                else
                {
                    physicalClaims[physicalKey] =
                        item.DownloadBase.Id;
                }
            }

            var persisted =
                await session.GetUnfinishedAsync(
                        cancellationToken)
                    .ConfigureAwait(true);

            if (persisted.Count !=
                iterations - admissionExceptions)
            {
                newFindings.Add(
                    $"Physical alias stress persisted count mismatch. " +
                    $"Expected {iterations - admissionExceptions}, " +
                    $"actual {persisted.Count}.");
            }

            if (duplicatePhysicalClaims > 0)
            {
                knownFindings.Add(
                    $"#176 family: physical alias stress produced " +
                    $"{duplicatePhysicalClaims} duplicate physical " +
                    $"claims across {iterations} admissions.");
            }
            else
            {
                report.Add(
                    "PASS physical alias stress: no duplicate physical " +
                    "destination was independently claimed.");
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static string MapToKnownPhysicalTarget(
        string path,
        string realRoot,
        IReadOnlyList<(string Name, string Root)> aliasRoots)
    {
        var fullPath =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));

        var normalizedRealRoot =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(realRoot));

        foreach (var alias in aliasRoots)
        {
            if (string.Equals(
                    alias.Name,
                    "real",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var aliasRoot =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(alias.Root));

            if (string.Equals(
                    fullPath,
                    aliasRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return normalizedRealRoot.ToUpperInvariant();
            }

            var prefix =
                aliasRoot
                + Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                var remainder =
                    fullPath[aliasRoot.Length..];

                return (
                    normalizedRealRoot
                    + remainder)
                    .ToUpperInvariant();
            }
        }

        return fullPath.ToUpperInvariant();
    }

    private static async Task CreateJunctionAsync(
        string junctionPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var startInfo =
            new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start cmd.exe for junction creation.");

        var stdoutTask =
            process.StandardOutput.ReadToEndAsync(
                cancellationToken);

        var stderrTask =
            process.StandardError.ReadToEndAsync(
                cancellationToken);

        await process.WaitForExitAsync(
                cancellationToken)
            .ConfigureAwait(true);

        var stdout =
            await stdoutTask.ConfigureAwait(true);

        var stderr =
            await stderrTask.ConfigureAwait(true);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mklink /J failed with exit code {process.ExitCode}. " +
                $"stdout: {stdout} stderr: {stderr}");
        }

        if (!Directory.Exists(junctionPath))
        {
            throw new InvalidOperationException(
                "mklink reported success but junction does not exist.");
        }
    }

    private static bool TryCreateDirectorySymlink(
        string linkPath,
        string targetPath,
        ICollection<string> report)
    {
        try
        {
            Directory.CreateSymbolicLink(
                linkPath,
                targetPath);

            if (Directory.Exists(linkPath))
            {
                report.Add(
                    "SETUP PASS: directory symbolic link created.");
                return true;
            }

            report.Add(
                "SETUP OBSERVATION: symbolic-link creation returned " +
                "without an accessible link.");

            return false;
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException
                  or IOException
                  or NotSupportedException)
        {
            report.Add(
                $"SETUP OBSERVATION: symbolic-link test unavailable: " +
                $"{exception.GetType().Name}: {exception.Message}");

            return false;
        }
    }

    private static bool TryDeleteDirectoryLink(
        string path,
        bool expectedToExist,
        ICollection<string> report)
    {
        if (!expectedToExist)
        {
            return true;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }

            return true;
        }
        catch (Exception exception)
        {
            report.Add(
                $"CLEANUP OBSERVATION: failed to remove link '{path}': " +
                $"{exception.GetType().Name}: {exception.Message}");

            return false;
        }
    }

    private static DownloadingItem CreateItem(
        string id,
        string basePath)
    {
        return new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = id,
                Bvid = $"BV-{id}",
                MainTitle = id,
                Name = id,
                FilePath = basePath
            },
            Downloading = new Downloading
            {
                Id = id,
                DownloadStatus =
                    DownloadStatus.WaitForDownload
            },
            PlayUrl = new PlayUrl()
        };
    }

    private sealed class AdmissionSession : IDisposable
    {
        private readonly SqliteDownloadTaskStore _store;

        private readonly DownloadTaskApplicationService _tasks;

        private readonly DownloadTaskProjectionStore _projections;

        private readonly DownloadTaskAdmissionService _admission;

        public AdmissionSession(string databasePath)
        {
            var directory =
                Path.GetDirectoryName(databasePath)
                ?? throw new InvalidOperationException(
                    "Database path has no parent directory.");

            Directory.CreateDirectory(directory);

            var clock = new SystemClock();

            _store =
                new SqliteDownloadTaskStore(
                    new SqliteDownloadTaskStoreOptions(
                        databasePath),
                    clock);

            _tasks =
                new DownloadTaskApplicationService(
                    _store,
                    clock);

            _projections =
                new DownloadTaskProjectionStore(
                    _tasks,
                    clock);

            _admission =
                new DownloadTaskAdmissionService(
                    new DownloadListState(),
                    _tasks,
                    _projections,
                    new RecordingDownloadTaskQueue());
        }

        public Task AdmitAsync(
            DownloadingItem item,
            bool autoAddNumberSuffix,
            CancellationToken cancellationToken)
        {
            return _admission.AdmitAsync(
                item,
                autoAddNumberSuffix,
                cancellationToken);
        }

        public Task<IReadOnlyList<DownloadTask>>
            GetUnfinishedAsync(
                CancellationToken cancellationToken)
        {
            return _tasks.GetUnfinishedAsync(
                cancellationToken);
        }

        public void Dispose()
        {
            _admission.Dispose();
            _projections.Dispose();
            _tasks.Dispose();
            _store.Dispose();
        }
    }
}
