using System.Text.Json;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class RidCapabilityArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void CurrentReleaseRidsHaveTheCapabilitiesTheirConsumersRequire()
    {
        var model = ReadCurrentModel();

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void PackageRidWithoutAssetsFailsWithoutRequiringUnrelatedPlatformCapabilities()
    {
        var model = ReadCurrentModel();
        const string unsupportedPackageRid = "freebsd-x64";
        var mutation = model with
        {
            PackageRids = Add(model.PackageRids, unsupportedPackageRid),
        };

        var failures = Validate(mutation);

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
        var model = ReadCurrentModel();
        const string ffmpegOnlyRid = "linux-s390x";
        var mutation = model with
        {
            FfmpegRequiredRids = Add(model.FfmpegRequiredRids, ffmpegOnlyRid),
            FfmpegAssetRids = Add(model.FfmpegAssetRids, ffmpegOnlyRid),
            FfmpegExtractionRids = Add(model.FfmpegExtractionRids, ffmpegOnlyRid),
        };

        Assert.Empty(Validate(mutation));
    }

    private static RidCapabilityModel ReadCurrentModel()
    {
        var manifestPath = Path.Combine(
            RepositoryRoot,
            "script",
            "assets",
            "external-assets.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        var ariaAssetRids = ReadObjectKeys(root.GetProperty("aria2").GetProperty("assets"));
        var ffmpeg = root.GetProperty("ffmpeg");
        var ffmpegAssetRids = ReadObjectKeys(ffmpeg.GetProperty("assets"));
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

        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var publishJobs = ReadPublishJobs(workflow);
        var packageRids = publishJobs
            .SelectMany(job => job.Rids)
            .ToHashSet(StringComparer.Ordinal);
        var signingRids = publishJobs
            .Where(job =>
                job.Source.Contains("./sign.sh", StringComparison.Ordinal) &&
                job.Source.Contains("./verify-app.sh", StringComparison.Ordinal))
            .SelectMany(job => job.Rids)
            .ToHashSet(StringComparer.Ordinal);
        var notarizationRids = publishJobs
            .Where(job =>
                job.Source.Contains("xcrun notarytool submit", StringComparison.Ordinal) &&
                job.Source.Contains("./verify-dmg.sh", StringComparison.Ordinal))
            .SelectMany(job => job.Rids)
            .ToHashSet(StringComparer.Ordinal);

        var qualityWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "quality.yml"));
        var tlsRids = ReadMatrixRids(ReadJobSource(qualityWorkflow, "aria2-tls-security"));
        var ffmpegPowerShell = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "ffmpeg.ps1"));
        var ffmpegShell = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "ffmpeg.sh"));
        var ariaPowerShell = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "aria2.ps1"));
        var ariaShell = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "aria2.sh"));
        var project = XDocument.Load(Path.Combine(RepositoryRoot, "DownKyi", "DownKyi.csproj"));
        var publishValidator = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "validate-publish-output.ps1"));

        return new RidCapabilityModel(
            packageRids,
            ariaAssetRids,
            ReadAriaExtractionRids(ariaAssetRids, ariaPowerShell, ariaShell),
            ffmpegRequiredRids,
            ffmpegAssetRids,
            ReadTemplateExtractionRids(ffmpegRequiredRids, ffmpegPowerShell, ffmpegShell),
            tlsRids,
            ffprobeCompanionRids,
            signingRids,
            notarizationRids,
            HasRuntimeAssetSelection(project),
            HasPackagedToolRequirement(publishValidator, "aria2/aria2c"),
            HasPackagedToolRequirement(publishValidator, "ffmpeg/ffmpeg"),
            HasPackagedToolRequirement(publishValidator, "ffmpeg/ffprobe"));
    }

    private static List<string> Validate(RidCapabilityModel model)
    {
        var failures = new List<string>();
        foreach (var rid in model.PackageRids)
        {
            if (!model.HasRuntimeAssetSelection)
            {
                failures.Add($"Package RID {rid} cannot select runtime assets from the SDK RuntimeIdentifier.");
            }

            if (!model.PublishValidatorRequiresAria2 ||
                !model.AriaAssetRids.Contains(rid) ||
                !model.AriaExtractionRids.Contains(rid))
            {
                failures.Add($"Package RID {rid} is missing its required aria2 capability.");
            }

            if (!model.PublishValidatorRequiresFfmpeg ||
                !model.PublishValidatorRequiresFfprobe ||
                !model.FfmpegAssetRids.Contains(rid) ||
                !model.FfmpegExtractionRids.Contains(rid))
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

    private static HashSet<string> ReadObjectKeys(JsonElement element)
    {
        return element.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static PublishJob[] ReadPublishJobs(string workflow)
    {
        return ReadJobs(workflow)
            .Where(job => job.Source.Contains("dotnet publish", StringComparison.Ordinal))
            .Select(job => new PublishJob(job.Source, ReadPublishRids(job.Source)))
            .ToArray();
    }

    private static HashSet<string> ReadPublishRids(string jobSource)
    {
        var cpuValues = ReadCpuValues(jobSource);
        var rids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(jobSource))
        {
            var remaining = line;
            while (TryReadRidOption(remaining, out var rid, out var consumed))
            {
                remaining = remaining[consumed..];
                if (rid.Contains("${{ matrix.cpu }}", StringComparison.Ordinal))
                {
                    foreach (var cpu in cpuValues)
                    {
                        rids.Add(rid.Replace("${{ matrix.cpu }}", cpu, StringComparison.Ordinal));
                    }
                }
                else
                {
                    rids.Add(rid);
                }
            }
        }

        return rids;
    }

    private static bool TryReadRidOption(string source, out string rid, out int consumed)
    {
        const string option = "-r ";
        var optionIndex = source.IndexOf(option, StringComparison.Ordinal);
        if (optionIndex < 0)
        {
            rid = string.Empty;
            consumed = source.Length;
            return false;
        }

        var start = optionIndex + option.Length;
        while (start < source.Length && (source[start] == '\'' || source[start] == '"'))
        {
            start++;
        }

        const string matrixCpu = "${{ matrix.cpu }}";
        var matrixEnd = source.IndexOf(matrixCpu, start, StringComparison.Ordinal);
        int end;
        if (matrixEnd >= start)
        {
            end = matrixEnd + matrixCpu.Length;
        }
        else
        {
            end = start;
            while (end < source.Length &&
                   !char.IsWhiteSpace(source[end]) &&
                   source[end] != '\'' &&
                   source[end] != '"')
            {
                end++;
            }
        }

        rid = source[start..end];
        consumed = end;
        return rid.Contains('-', StringComparison.Ordinal);
    }

    private static HashSet<string> ReadCpuValues(string jobSource)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(jobSource).Select(line => line.Trim()))
        {
            if (line.StartsWith("cpu: [", StringComparison.Ordinal) && line.EndsWith(']'))
            {
                var start = line.IndexOf('[', StringComparison.Ordinal) + 1;
                foreach (var value in line[start..^1].Split(','))
                {
                    values.Add(value.Trim());
                }
            }
            else if (line.StartsWith("- cpu: ", StringComparison.Ordinal))
            {
                values.Add(line[7..].Trim());
            }
        }

        return values;
    }

    private static HashSet<string> ReadMatrixRids(string jobSource)
    {
        const string prefix = "- rid: ";
        return Lines(jobSource)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..].Trim())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadTemplateExtractionRids(
        IReadOnlySet<string> candidateRids,
        params string[] scripts)
    {
        var prefixes = ReadRidTemplatePrefixes(scripts);
        return candidateRids
            .Where(rid => prefixes.Any(prefix => rid.StartsWith($"{prefix}-", StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadAriaExtractionRids(
        IReadOnlySet<string> assetRids,
        string powerShell,
        string shell)
    {
        var supported = ReadTemplateExtractionRids(assetRids, powerShell);
        if (shell.Contains("download_aria2 \"$@\"", StringComparison.Ordinal) &&
            shell.Contains("manifest[\"aria2\"][\"assets\"]", StringComparison.Ordinal))
        {
            supported.UnionWith(assetRids.Where(rid => !rid.StartsWith("win-", StringComparison.Ordinal)));
        }

        return supported;
    }

    private static HashSet<string> ReadRidTemplatePrefixes(params string[] scripts)
    {
        const string suffix = "-$arch";
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in scripts.SelectMany(Lines))
        {
            var suffixIndex = line.IndexOf(suffix, StringComparison.Ordinal);
            if (suffixIndex < 0)
            {
                continue;
            }

            var quoteIndex = line.LastIndexOf('"', suffixIndex);
            if (quoteIndex >= 0)
            {
                prefixes.Add(line[(quoteIndex + 1)..suffixIndex]);
            }
        }

        return prefixes;
    }

    private static bool HasRuntimeAssetSelection(XDocument project)
    {
        var runtimeSelection = project.Descendants()
            .Where(element => element.Name.LocalName == "DownKyiAssetRuntimeIdentifier")
            .SingleOrDefault(element =>
                string.Equals(element.Value, "$(RuntimeIdentifier)", StringComparison.Ordinal));
        if (runtimeSelection?.Attribute("Condition")?.Value.Contains(
                "'$(RuntimeIdentifier)' != ''",
                StringComparison.Ordinal) != true)
        {
            return false;
        }

        var includes = project.Descendants()
            .Where(element => element.Name.LocalName == "None")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .ToArray();
        return includes.Any(include => include.Contains(
                   @"Binary\$(DownKyiAssetRuntimeIdentifier)\aria2\*",
                   StringComparison.Ordinal)) &&
               includes.Any(include => include.Contains(
                   @"Binary\$(DownKyiAssetRuntimeIdentifier)\ffmpeg\*",
                   StringComparison.Ordinal));
    }

    private static bool HasPackagedToolRequirement(string validator, string relativePath)
    {
        return validator.Contains(relativePath, StringComparison.Ordinal);
    }

    private static List<WorkflowJob> ReadJobs(string workflow)
    {
        var lines = Lines(workflow).ToArray();
        var jobs = new List<WorkflowJob>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (!IsJobHeader(lines[index]))
            {
                continue;
            }

            var end = index + 1;
            while (end < lines.Length && !IsJobHeader(lines[end]))
            {
                end++;
            }

            jobs.Add(new WorkflowJob(
                lines[index].Trim()[..^1],
                string.Join('\n', lines[index..end])));
            index = end - 1;
        }

        return jobs;
    }

    private static string ReadJobSource(string workflow, string name)
    {
        return Assert.Single(ReadJobs(workflow), job =>
            string.Equals(job.Name, name, StringComparison.Ordinal)).Source;
    }

    private static bool IsJobHeader(string line)
    {
        return line.Length > 3 &&
               line.StartsWith("  ", StringComparison.Ordinal) &&
               !line.StartsWith("   ", StringComparison.Ordinal) &&
               line.EndsWith(':') &&
               !line.TrimStart().StartsWith('-');
    }

    private static IEnumerable<string> Lines(string source)
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }

    private sealed record WorkflowJob(string Name, string Source);

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
        HashSet<string> NotarizationRids,
        bool HasRuntimeAssetSelection,
        bool PublishValidatorRequiresAria2,
        bool PublishValidatorRequiresFfmpeg,
        bool PublishValidatorRequiresFfprobe);
}
