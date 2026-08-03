using System.Security.Cryptography;

namespace DownKyi.Core.Aria2cNet.Server;

internal static class AriaBinaryIntegrityVerifier
{
    internal const string ChecksumSidecarSuffix = ".sha256";

    internal static void Verify(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "The packaged aria2 executable is missing.",
                executablePath);
        }

        var checksumPath = executablePath + ChecksumSidecarSuffix;
        if (!File.Exists(checksumPath))
        {
            throw new FileNotFoundException(
                "The packaged aria2 checksum sidecar is missing.",
                checksumPath);
        }

        var checksumText = File.ReadAllText(checksumPath).Trim();
        if (checksumText.Length != SHA256.HashSizeInBytes * 2)
        {
            throw new InvalidDataException(
                "The packaged aria2 checksum sidecar is malformed.");
        }

        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(checksumText);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The packaged aria2 checksum sidecar is malformed.",
                exception);
        }

        using var executable = File.OpenRead(executablePath);
        var actualHash = SHA256.HashData(executable);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException(
                "The packaged aria2 executable failed its integrity check.");
        }
    }
}
