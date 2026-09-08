using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed partial class ReleaseWorkflowArchitectureTests
{
    [Fact]
    public void CurrentReleaseRidsHaveTheCapabilitiesTheirConsumersRequire()
    {
        var model = ReadCurrentRidCapabilityModel();

        Assert.Empty(ValidateRidCapabilities(model));
    }

    [Fact]
    public void PackageRidWithoutAssetsFailsWithoutRequiringUnrelatedPlatformCapabilities()
    {
        var model = ReadCurrentRidCapabilityModel();
        const string unsupportedPackageRid = "freebsd-x64";
        var mutation = model with
        {
            PackageRids = Add(model.PackageRids, unsupportedPackageRid),
        };

        var failures = ValidateRidCapabilities(mutation);

        Assert.Contains(failures, failure =>
            failure.Contains(unsupportedPackageRid, StringComparison.Ordinal) &&
            failure.Contains("aria2", StringComparison.Ordinal));
        Assert.Contains(failures, failure =>
            failure.Contains(unsupportedPackageRid, StringComparison.Ordinal) &&
            failure.Contains("FFmpeg", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, failure =>
            failure.Contains(unsupportedPackageRid, StringComparison.Ordinal) &&
            (failure.Contains("ffprobe companion", StringComparison.Ordinal) ||
             failure.Contains("signing", StringComparison.Ordinal) ||
             failure.Contains("notarization", StringComparison.Ordinal)));
    }

    [Fact]
    public void CapabilityContractAllowsDifferentPurposeRidSets()
    {
        var model = ReadCurrentRidCapabilityModel();
        const string ffmpegOnlyRid = "linux-s390x";
        var mutation = model with
        {
            FfmpegRequiredRids = Add(model.FfmpegRequiredRids, ffmpegOnlyRid),
            FfmpegAssetRids = Add(model.FfmpegAssetRids, ffmpegOnlyRid),
            FfmpegExtractionRids = Add(model.FfmpegExtractionRids, ffmpegOnlyRid),
        };

        Assert.Empty(ValidateRidCapabilities(mutation));
    }

    private static RidCapabilityModel ReadCurrentRidCapabilityModel()
    {
        var manifestPath = Path.Combine(
            RepositoryRoot,
            "script",
            "assets",
            "external-assets.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        var ariaAssetRids = ReadRidKeys(root.GetProperty("aria2").GetProperty("assets"));
        var ffmpeg = root.GetProperty("ffmpeg");
        var ffmpegAssetRids = ReadRidKeys(ffmpeg.GetProperty("assets"));
        var ffmpegRequiredRids = ffmpeg.GetProperty("requiredRids")
            .EnumerateArray()
            .Select(item => item.GetString())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var ffprobeCompanionRids = ffmpeg.GetProperty("assets")
            .EnumerateObject()
            .Where(asset =>
                asset.Value.TryGetProperty("ffprobeUrl", out _) &&
                asset.Value.TryGetProperty("ffprobeSha256", out _) &&
                asset.Value.TryGetProperty("ffprobeFileName", out _))
            .Select(asset => asset.Name)
            .ToHashSet(StringComparer.Ordinal);

        var buildWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var publishJobs = ReadPublishJobs(buildWorkflow);
        var packageRids = publishJobs
            .SelectMany(job => job.Rids)
            .ToHashSet(StringComparer.Ordinal);
        var signingRids = RidsForJobsContaining(publishJobs, "./sign.sh", "./verify-app.sh");
        var notarizationRids = RidsForJobsContaining(
            publishJobs,
            "xcrun notarytool submit",
            "./verify-dmg.sh");

        var qualityWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "quality.yml"));
        var qualityLines = Lines(qualityWorkflow);
        var tlsJob = GetYamlBlock(qualityLines, "  aria2-tls-security:", 2);
        var tlsRids = MatrixRidPattern().Matches(string.Join('\n', tlsJob))
            .Select(match => match.Groups["rid"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var ffmpegPowerShell = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "ffmpeg.ps1"));
        var ffmpegShell = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "ffmpeg.sh"));
        var ariaPowerShell = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "aria2.ps1"));
        var ariaShell = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "aria2.sh"));

        return new RidCapabilityModel(
            packageRids,
            ariaAssetRids,
            ReadAriaExtractionRids(ariaAssetRids, ariaPowerShell, ariaShell),
            ffmpegRequiredRids,
            ffmpegAssetRids,
            ReadFfmpegExtractionRids(ffmpegAssetRids, ffmpegPowerShell, ffmpegShell),
            tlsRids,
            ffprobeCompanionRids,
            signingRids,
            notarizationRids);
    }

    private static List<string> ValidateRidCapabilities(RidCapabilityModel model)
    {
        var failures = new List<string>();
        foreach (var rid in model.PackageRids)
        {
            if (!model.AriaAssetRids.Contains(rid) || !model.AriaExtractionRids.Contains(rid))
            {
                failures.Add($"Package RID {rid} is missing its required aria2 capability.");
            }

            if (!model.FfmpegAssetRids.Contains(rid) || !model.FfmpegExtractionRids.Contains(rid))
            {
                failures.Add($"Package RID {rid} is missing its required FFmpeg/ffprobe capability.");
            }
        }

        foreach (var rid in model.FfmpegRequiredRids)
        {
            if (!model.FfmpegAssetRids.Contains(rid))
            {
                failures.Add($"ffmpeg.requiredRids entry {rid} has no FFmpeg asset.");
            }

            if (!model.FfmpegExtractionRids.Contains(rid))
            {
                failures.Add($"ffmpeg.requiredRids entry {rid} has no extraction runner.");
            }
        }

        foreach (var rid in model.TlsRids)
        {
            if (!model.AriaAssetRids.Contains(rid) || !model.AriaExtractionRids.Contains(rid))
            {
                failures.Add($"TLS matrix RID {rid} has no aria2 capability.");
            }
        }

        var macRidsRequiringCompanion = model.PackageRids
            .Concat(model.FfmpegRequiredRids)
            .Where(IsMacRid)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var rid in macRidsRequiringCompanion)
        {
            if (!model.FfprobeCompanionRids.Contains(rid))
            {
                failures.Add($"macOS RID {rid} has no ffprobe companion asset.");
            }
        }

        foreach (var rid in model.PackageRids.Where(IsMacRid))
        {
            if (!model.SigningRids.Contains(rid))
            {
                failures.Add($"macOS package RID {rid} has no signing capability.");
            }

            if (!model.NotarizationRids.Contains(rid))
            {
                failures.Add($"macOS package RID {rid} has no notarization capability.");
            }
        }

        return failures;
    }

    private static HashSet<string> ReadRidKeys(JsonElement element)
    {
        return element.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static PublishJob[] ReadPublishJobs(string workflow)
    {
        var lines = Lines(workflow);
        return lines
            .Where(line =>
                GetIndent(line) == 2 &&
                line.EndsWith(':') &&
                !line.TrimStart().StartsWith('-'))
            .Select(header => string.Join('\n', GetYamlBlock(lines, header, 2)))
            .Where(source => source.Contains("dotnet publish", StringComparison.Ordinal))
            .Select(source => new PublishJob(source, ReadPublishRids(source)))
            .ToArray();
    }

    private static HashSet<string> ReadPublishRids(string jobSource)
    {
        var cpuValues = InlineCpuPattern().Matches(jobSource)
            .SelectMany(match => match.Groups["values"].Value.Split(','))
            .Select(value => value.Trim())
            .Concat(IncludeCpuPattern().Matches(jobSource)
                .Select(match => match.Groups["value"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var rids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in RuntimeIdentifierPattern().Matches(jobSource))
        {
            var rid = match.Groups["rid"].Value;
            if (MatrixCpuPattern().IsMatch(rid))
            {
                foreach (var cpu in cpuValues)
                {
                    rids.Add(MatrixCpuPattern().Replace(rid, cpu));
                }
            }
            else
            {
                rids.Add(rid);
            }
        }

        return rids;
    }

    private static HashSet<string> RidsForJobsContaining(
        IEnumerable<PublishJob> jobs,
        params string[] markers)
    {
        return jobs
            .Where(job => markers.All(marker => job.Source.Contains(marker, StringComparison.Ordinal)))
            .SelectMany(job => job.Rids)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadFfmpegExtractionRids(
        IEnumerable<string> assetRids,
        string powerShell,
        string shell)
    {
        var supportsWindows = powerShell.Contains("$rid = \"win-$arch\"", StringComparison.Ordinal);
        var supportsLinux = shell.Contains("local rid=\"linux-$arch\"", StringComparison.Ordinal) &&
                            shell.Contains("[ \"$os\" == \"linux\" ]", StringComparison.Ordinal);
        var supportsMac = shell.Contains("local rid=\"osx-$arch\"", StringComparison.Ordinal) &&
                          shell.Contains("[ \"$os\" == \"mac\" ]", StringComparison.Ordinal);

        return assetRids.Where(rid =>
                (supportsWindows && rid.StartsWith("win-", StringComparison.Ordinal)) ||
                (supportsLinux && rid.StartsWith("linux-", StringComparison.Ordinal)) ||
                (supportsMac && IsMacRid(rid)))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadAriaExtractionRids(
        IEnumerable<string> assetRids,
        string powerShell,
        string shell)
    {
        var supportsWindows = powerShell.Contains("$rid = \"win-$arch\"", StringComparison.Ordinal);
        var supportsUnix = shell.Contains("download_aria2 \"$@\"", StringComparison.Ordinal) &&
                           shell.Contains("manifest[\"aria2\"][\"assets\"]", StringComparison.Ordinal);

        return assetRids.Where(rid =>
                (supportsWindows && rid.StartsWith("win-", StringComparison.Ordinal)) ||
                (supportsUnix && !rid.StartsWith("win-", StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string[] Lines(string source)
    {
        return source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    private static bool IsMacRid(string rid)
    {
        return rid.StartsWith("osx-", StringComparison.Ordinal);
    }

    private static HashSet<string> Add(IReadOnlySet<string> source, string value)
    {
        var result = source.ToHashSet(StringComparer.Ordinal);
        result.Add(value);
        return result;
    }

    [GeneratedRegex(@"(?m)^\s*cpu:\s*\[\s*(?<values>[^\]]+)\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCpuPattern();

    [GeneratedRegex(@"(?m)^\s*-\s*cpu:\s*(?<value>[a-z0-9]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex IncludeCpuPattern();

    [GeneratedRegex(@"(?:-r|--runtime(?:-identifier)?)\s+['""]?(?<rid>[a-z0-9]+-(?:\$\{\{\s*matrix\.cpu\s*\}\}|[a-z0-9]+))", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeIdentifierPattern();

    [GeneratedRegex(@"\$\{\{\s*matrix\.cpu\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex MatrixCpuPattern();

    [GeneratedRegex(@"(?m)^\s*-\s*rid:\s*(?<rid>[a-z0-9-]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MatrixRidPattern();

    private sealed record PublishJob(string Source, HashSet<string> Rids);

    private sealed record RidCapabilityModel(
        HashSet<string> PackageRids,
        HashSet<string> AriaAssetRids,
        HashSet<string> AriaExtractionRids,
        HashSet<string> FfmpegRequiredRids,
        HashSet<string> FfmpegAssetRids,
        HashSet<string> FfmpegExtractionRids,
        HashSet<string> TlsRids,
        HashSet<string> FfprobeCompanionRids,
        HashSet<string> SigningRids,
        HashSet<string> NotarizationRids);
}
