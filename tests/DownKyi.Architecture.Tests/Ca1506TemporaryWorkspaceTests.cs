using System.Diagnostics;
using System.Text;

namespace DownKyi.Architecture.Tests;

public sealed class Ca1506TemporaryWorkspaceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ModulePath = Path.Combine(
        RepositoryRoot,
        "script",
        "code-metrics",
        "Ca1506TemporaryWorkspace.psm1");

    [Fact]
    public async Task ImmediateCleanupRemovesWorkspaceOrPreservesItWithSanitizedFailure()
    {
        using var directory = new TemporaryDirectory();
        var result = await RunPowerShellAsync(
            directory.Path,
            """
            Import-Module $env:DOWNKYI_TEST_MODULE -Force
            $workspace = New-Ca1506TemporaryWorkspace -TemporaryBasePath $env:DOWNKYI_TEST_ROOT
            if (-not [IO.Directory]::Exists($workspace.OperationDirectory)) {
                throw 'The operation directory was not created.'
            }
            if (-not (Remove-Ca1506TemporaryWorkspace `
                    -OwnershipRoot $workspace.OwnershipRoot `
                    -OperationDirectory $workspace.OperationDirectory)) {
                throw 'Immediate cleanup unexpectedly failed.'
            }
            if ([IO.Directory]::Exists($workspace.OperationDirectory)) {
                throw 'Immediate cleanup left the operation directory behind.'
            }

            $preserved = New-Ca1506TemporaryWorkspace -TemporaryBasePath $env:DOWNKYI_TEST_ROOT
            $removed = Remove-Ca1506TemporaryWorkspace `
                -OwnershipRoot $preserved.OwnershipRoot `
                -OperationDirectory $preserved.OperationDirectory `
                -DeleteDirectory { throw [IO.IOException]::new('Injected sharing failure.') }
            if ($removed -or -not [IO.Directory]::Exists($preserved.OperationDirectory)) {
                throw 'A failed immediate cleanup did not preserve its operation directory.'
            }
            Write-Output 'CA1506 audit temporary workspace cleanup failed.'
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("CA1506 audit temporary workspace cleanup failed.", result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
        Assert.DoesNotContain(directory.Path, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RepositoryRoot, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextExecutionScavengesOnlyExpiredOwnedDirectoriesAndContinuesAfterFailure()
    {
        using var directory = new TemporaryDirectory();
        var result = await RunPowerShellAsync(
            directory.Path,
            """
            Import-Module $env:DOWNKYI_TEST_MODULE -Force
            $now = [DateTimeOffset]::Parse('2026-09-06T00:00:00Z')
            $ownershipRoot = Join-Path $env:DOWNKYI_TEST_ROOT 'downkyi-ca1506'
            [IO.Directory]::CreateDirectory($ownershipRoot) | Out-Null
            $expiredName = '11111111111111111111111111111111'
            $lockedName = '22222222222222222222222222222222'
            $freshName = '33333333333333333333333333333333'
            $unownedName = 'not-an-operation'
            foreach ($name in @($expiredName, $lockedName, $freshName, $unownedName)) {
                [IO.Directory]::CreateDirectory((Join-Path $ownershipRoot $name)) | Out-Null
            }
            [IO.Directory]::SetLastWriteTimeUtc((Join-Path $ownershipRoot $expiredName), $now.UtcDateTime.AddHours(-25))
            [IO.Directory]::SetLastWriteTimeUtc((Join-Path $ownershipRoot $lockedName), $now.UtcDateTime.AddHours(-25))
            [IO.Directory]::SetLastWriteTimeUtc((Join-Path $ownershipRoot $freshName), $now.UtcDateTime.AddHours(-23))
            [IO.Directory]::SetLastWriteTimeUtc((Join-Path $ownershipRoot $unownedName), $now.UtcDateTime.AddDays(-7))
            $outside = Join-Path $env:DOWNKYI_TEST_ROOT 'unrelated-system-temp-directory'
            [IO.Directory]::CreateDirectory($outside) | Out-Null

            $deleteDirectory = {
                param($Path)
                if ([IO.Path]::GetFileName($Path) -eq $lockedName) {
                    throw [IO.IOException]::new('Injected stale sharing failure.')
                }
                [IO.Directory]::Delete($Path, $true)
            }.GetNewClosure()
            $workspace = New-Ca1506TemporaryWorkspace `
                -TemporaryBasePath $env:DOWNKYI_TEST_ROOT `
                -UtcNow $now `
                -DeleteDirectory $deleteDirectory

            if ([IO.Directory]::Exists((Join-Path $ownershipRoot $expiredName))) {
                throw 'The expired operation was not scavenged.'
            }
            if (-not [IO.Directory]::Exists((Join-Path $ownershipRoot $lockedName))) {
                throw 'The locked expired operation was not preserved.'
            }
            if (-not [IO.Directory]::Exists((Join-Path $ownershipRoot $freshName))) {
                throw 'The operation inside the TTL was deleted.'
            }
            if (-not [IO.Directory]::Exists((Join-Path $ownershipRoot $unownedName))) {
                throw 'The unowned directory was deleted.'
            }
            if (-not [IO.Directory]::Exists($outside)) {
                throw 'A directory outside the ownership root was deleted.'
            }
            if (-not (Remove-Ca1506TemporaryWorkspace `
                    -OwnershipRoot $workspace.OwnershipRoot `
                    -OperationDirectory $workspace.OperationDirectory)) {
                throw 'The current operation could not be cleaned up.'
            }
            """);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task PathEscapeAndReparsePointCandidatesFailSafeWithoutInvokingDelete()
    {
        using var directory = new TemporaryDirectory();
        var result = await RunPowerShellAsync(
            directory.Path,
            """
            Import-Module $env:DOWNKYI_TEST_MODULE -Force
            $ownershipRoot = Join-Path $env:DOWNKYI_TEST_ROOT 'downkyi-ca1506'
            [IO.Directory]::CreateDirectory($ownershipRoot) | Out-Null
            $outside = Join-Path $env:DOWNKYI_TEST_ROOT '44444444444444444444444444444444'
            $reparseCandidate = Join-Path $ownershipRoot '55555555555555555555555555555555'
            [IO.Directory]::CreateDirectory($outside) | Out-Null
            [IO.Directory]::CreateDirectory($reparseCandidate) | Out-Null
            $sentinel = Join-Path $env:DOWNKYI_TEST_ROOT 'delete-was-invoked'
            $deleteDirectory = {
                param($Path)
                [IO.File]::WriteAllText($sentinel, $Path)
                [IO.Directory]::Delete($Path, $true)
            }.GetNewClosure()

            if (Remove-Ca1506TemporaryWorkspace `
                    -OwnershipRoot $ownershipRoot `
                    -OperationDirectory $outside `
                    -DeleteDirectory $deleteDirectory) {
                throw 'A path outside the ownership root was accepted.'
            }
            if (Test-Ca1506OwnedOperationPath `
                    -OwnershipRoot $ownershipRoot `
                    -OperationDirectory (Join-Path $ownershipRoot 'child/../66666666666666666666666666666666') `
                    -Attributes ([IO.FileAttributes]::Directory)) {
                throw 'A path containing dot-dot was accepted.'
            }
            if (Remove-Ca1506TemporaryWorkspace `
                    -OwnershipRoot $ownershipRoot `
                    -OperationDirectory $reparseCandidate `
                    -GetAttributes { [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint } `
                    -DeleteDirectory $deleteDirectory) {
                throw 'A reparse-point operation directory was accepted.'
            }
            if ([IO.File]::Exists($sentinel)) {
                throw 'Delete was invoked for an unsafe candidate.'
            }
            if (-not [IO.Directory]::Exists($outside) -or
                -not [IO.Directory]::Exists($reparseCandidate)) {
                throw 'An unsafe candidate was modified.'
            }
            """);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(string.Empty, result.StandardError);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string temporaryRoot, string script)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        process.StartInfo.Environment["DOWNKYI_TEST_MODULE"] = ModulePath;
        process.StartInfo.Environment["DOWNKYI_TEST_ROOT"] = temporaryRoot;

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"downkyi-ca1506-workspace-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
