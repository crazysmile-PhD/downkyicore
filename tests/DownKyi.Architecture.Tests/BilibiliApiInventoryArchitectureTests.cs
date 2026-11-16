using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

[Collection(PowerShellProcessTestGroup.Name)]
public sealed partial class BilibiliApiInventoryArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void GeneratedSourceInventoryMatchesEveryHardCodedBilibiliApiEndpoint()
    {
        var inventory = RunAuditScript(
            "audit-bilibili-api.ps1",
            "-GenerateSourceInventory");
        using var document = JsonDocument.Parse(inventory);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal("src/DownKyi.Core/BiliApi", root.GetProperty("SourceRoot").GetString());

        var generatedEndpoints = root.GetProperty("Endpoints")
            .EnumerateArray()
            .Select(item => item.GetProperty("Endpoint").GetString())
            .ToArray();
        foreach (var item in root.GetProperty("Endpoints").EnumerateArray())
        {
            var endpoint = item.GetProperty("Endpoint").GetString();
            var locations = item.GetProperty("Locations").EnumerateArray().ToArray();
            Assert.NotEmpty(locations);
            foreach (var location in locations)
            {
                var relativePath = location.GetProperty("Path").GetString();
                var lineNumber = location.GetProperty("Line").GetInt32();
                var sourcePath = Path.Combine(RepositoryRoot, relativePath!);
                var sourceLines = File.ReadAllLines(sourcePath);
                Assert.InRange(lineNumber, 1, sourceLines.Length);
                Assert.Contains(
                    $"https://{endpoint}",
                    sourceLines[lineNumber - 1],
                    StringComparison.Ordinal);
            }
        }

        var sourceEndpoints = Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", "DownKyi.Core", "BiliApi"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Models{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .SelectMany(ExtractEndpoints)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(sourceEndpoints, generatedEndpoints);
    }

    [Fact]
    public void AnonymousNonSuccessCodeExceptionIsScopedToNavigation()
    {
        var usages = Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", "DownKyi.Core"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(
                "RequestJsonAllowingCodeAsync<",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "src/DownKyi.Core/BiliApi/BiliApiRequest.cs",
                "src/DownKyi.Core/BiliApi/Users/UserInfo.cs"
            ],
            usages);
    }

    [Fact]
    public void OptionalJsonEnvelopeFieldsCannotInventPayloads()
    {
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(RepositoryRoot, "src", "DownKyi.Core", "BiliApi"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!IsOptionalEnvelopeAttribute(lines[index]))
                {
                    continue;
                }

                var declaration = lines[index];
                if (!declaration.Contains("public ", StringComparison.Ordinal)
                    && index + 1 < lines.Length)
                {
                    declaration += lines[index + 1];
                }

                var propertyEnd = declaration.IndexOf('}', StringComparison.Ordinal);
                if (propertyEnd >= 0)
                {
                    declaration = declaration[..(propertyEnd + 1)];
                }

                if (declaration.Contains("= new", StringComparison.Ordinal)
                    || declaration.Contains("= Array.Empty", StringComparison.Ordinal)
                    || declaration.Contains("= []", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{Path.GetRelativePath(RepositoryRoot, path)}:{index + 1}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Optional JSON envelopes must preserve missing fields: {string.Join(", ", violations)}");
    }

    [Fact]
    public void LiveProbeIsExplicitAndDoesNotLoadCookies()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "audit-bilibili-api.ps1"));

        Assert.Contains("[switch]$ConfirmLive", script, StringComparison.Ordinal);
        Assert.Contains("Authentication = 'anonymous", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SESSDATA", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GetLoginInfoCookies", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticatedLiveProbeContractIsExplicitSanitizedAndGeneratedOnDemand()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "audit-bilibili-authenticated-api.ps1"));
        var committedSnapshot = Path.Combine(
            RepositoryRoot,
            "docs",
            "operations",
            "bilibili-authenticated-api-audit.json");
        var artifact = RunAuditScript(
            "audit-bilibili-authenticated-api.ps1",
            "-GenerateContractSample");

        Assert.Contains("[switch]$ConfirmAuthenticatedLive", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$GenerateContractSample", script, StringComparison.Ordinal);
        Assert.Contains("$environmentVariableName = 'BILIBILI_TEST_COOKIE'", script, StringComparison.Ordinal);
        Assert.Contains("-EnvironmentVariableLoaded $true", script, StringComparison.Ordinal);
        Assert.Contains("$navJson.data.isLogin -eq $true", script, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLoginInfoCookies", script, StringComparison.Ordinal);
        Assert.Equal(1, SetContentPattern().Count(script));
        Assert.False(File.Exists(committedSnapshot));

        foreach (var sensitiveName in new[]
                 {
                     "BILIBILI_TEST_COOKIE",
                     "SESSDATA",
                     "bili_jct",
                     "DedeUserID",
                     "Cookie",
                     "Request Headers"
                 })
        {
            Assert.DoesNotContain(sensitiveName, artifact, StringComparison.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(artifact);
        var root = document.RootElement;
        Assert.False(root.GetProperty("EnvironmentVariableLoaded").GetBoolean());
        Assert.False(root.GetProperty("NavigationGatePassed").GetBoolean());
        Assert.Equal(
            [
                "Architecture",
                "CapturedAtUtc",
                "Commit",
                "EnvironmentVariableLoaded",
                "NavigationGatePassed",
                "OperatingSystem",
                "Results",
                "Runtime",
                "SchemaVersion"
            ],
            root.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());

        var results = root.GetProperty("Results").EnumerateArray().ToArray();
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            Assert.Equal(
                [
                    "BilibiliCode",
                    "ContractDrift",
                    "ErrorType",
                    "HttpStatus",
                    "Name",
                    "Outcome",
                    "Path",
                    "RequiredFieldsPresent",
                    "RequiresLogin",
                    "ResponseStructureMatchesExpected"
                ],
                result.EnumerateObject()
                    .Select(property => property.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            Assert.StartsWith("/", result.GetProperty("Path").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("?", result.GetProperty("Path").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("HttpStatus").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("BilibiliCode").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("ResponseStructureMatchesExpected").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("RequiredFieldsPresent").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("ContractDrift").ValueKind);
            Assert.Equal("indeterminate", result.GetProperty("Outcome").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("ErrorType").ValueKind);
        }
    }

    private static string RunAuditScript(string scriptName, string mode)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-{Guid.NewGuid():N}.json");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot, "script", scriptName));
            startInfo.ArgumentList.Add(mode);
            startInfo.ArgumentList.Add("-OutputPath");
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"{scriptName} {mode} failed: {standardError}{standardOutput}");

            return File.ReadAllText(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static bool IsOptionalEnvelopeAttribute(string line)
    {
        return line.Contains("[JsonProperty(\"data\")]", StringComparison.Ordinal)
               || line.Contains("[JsonProperty(\"result\")]", StringComparison.Ordinal)
               || line.Contains("[JsonPropertyName(\"data\")]", StringComparison.Ordinal)
               || line.Contains("[JsonPropertyName(\"result\")]", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ExtractEndpoints(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in EndpointPattern().Matches(line))
            {
                yield return $"{match.Groups["host"].Value}{match.Groups["path"].Value}";
            }
        }
    }

    [GeneratedRegex(
        "https://(?<host>(?:api|passport|space)\\.bilibili\\.com)(?<path>/(?:x|pgc|pugv|ajax)/[^\\\"?]*)",
        RegexOptions.CultureInvariant,
        1000)]
    private static partial Regex EndpointPattern();

    [GeneratedRegex("Set-Content", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SetContentPattern();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }
}
