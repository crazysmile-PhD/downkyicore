using DownKyi.Services.Account;

namespace DownKyi.Tests;

public sealed class LoginFileChangeMonitorTests
{
    public static TheoryData<string> Changes => new()
    {
        "create",
        "modify",
        "delete",
        "replace"
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task LoginFileChangeInvalidatesCookieCache(string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-login-watch-{Guid.NewGuid():N}");
        var loginPath = Path.Combine(directory, "Login");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            Directory.CreateDirectory(directory);
            if (change != "create")
            {
                await File.WriteAllTextAsync(
                    loginPath,
                    "initial",
                    TestContext.Current.CancellationToken);
            }

            using var monitor = new LoginFileChangeMonitor(loginPath, () => invalidated.TrySetResult());
            await monitor.StartAsync(TestContext.Current.CancellationToken);

            await ApplyChangeAsync(change, directory, loginPath);

            await invalidated.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static Task ApplyChangeAsync(string change, string directory, string loginPath)
    {
        return change switch
        {
            "create" => File.WriteAllTextAsync(
                loginPath,
                "created",
                TestContext.Current.CancellationToken),
            "modify" => File.WriteAllTextAsync(
                loginPath,
                "modified",
                TestContext.Current.CancellationToken),
            "delete" => Task.Run(
                () => File.Delete(loginPath),
                TestContext.Current.CancellationToken),
            "replace" => ReplaceAsync(directory, loginPath),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null)
        };
    }

    private static async Task ReplaceAsync(string directory, string loginPath)
    {
        var replacementPath = Path.Combine(directory, "Login-replacement");
        await File.WriteAllTextAsync(
            replacementPath,
            "replacement",
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        File.Move(replacementPath, loginPath, overwrite: true);
    }
}
