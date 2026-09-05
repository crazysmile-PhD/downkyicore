using System.Diagnostics;

namespace DownKyi.CodeMetricsAudit;

internal sealed record GitState(string Commit, bool DirtyWorktree);

internal static class GitStateReader
{
    public static async Task<GitState> ReadAsync(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var commit = await RunGitAsync(repositoryRoot, "rev-parse", "HEAD").ConfigureAwait(false);
        var status = await RunGitAsync(
                repositoryRoot,
                "status",
                "--porcelain",
                "--untracked-files=no")
            .ConfigureAwait(false);
        return new GitState(commit.Trim(), !string.IsNullOrWhiteSpace(status));
    }

    private static async Task<string> RunGitAsync(string repositoryRoot, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Git could not be started for the CA1506 audit.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git failed for the CA1506 audit with exit code {process.ExitCode}: {error.Trim()}");
        }

        return output;
    }
}
