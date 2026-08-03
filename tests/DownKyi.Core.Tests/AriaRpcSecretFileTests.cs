using DownKyi.Core.Aria2cNet.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

public sealed class AriaRpcSecretFileTests
{
    [Fact]
    public void SecretFileIsPrivateAndRemovedAfterStartupUse()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-aria-secret-{Guid.NewGuid():N}");
        const string secret = "test-secret-without-account-data";
        try
        {
            var lease = AriaRpcSecretFile.Create(
                directory,
                secret,
                NullLogger.Instance);
            var path = lease.Path;

            Assert.True(File.Exists(path));
            Assert.Equal($"rpc-secret={secret}{Environment.NewLine}", File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }

            lease.Dispose();

            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("test\rsecret")]
    [InlineData("test\nsecret")]
    [InlineData("test\0secret")]
    public void SecretFileRejectsControlCharacters(string secret)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-aria-secret-{Guid.NewGuid():N}");
        try
        {
            Assert.Throws<ArgumentException>(() => AriaRpcSecretFile.Create(
                directory,
                secret,
                NullLogger.Instance));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
