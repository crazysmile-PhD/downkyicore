using System.Security.Cryptography;

namespace DownKyi.ProcessSupervision;

internal sealed record IpcEndpointName
{
    private const string TokenAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const int EntropyByteCount = 10;

    public const string PhysicalIdentifierPrefix = "dkyi-";
    public const int TokenCharacterCount = 16;
    public const int GeneratedPhysicalIdentifierLength = 21;
    public const int MaximumPhysicalIdentifierLength = 24;
    public const int MacOsUnixDomainSocketPathLimitBytes = 104;
    public const int UnixDomainSocketTerminatorBytes = 1;
    public const int MaximumMacOsDotNetPipePrefixBytes =
        MacOsUnixDomainSocketPathLimitBytes -
        UnixDomainSocketTerminatorBytes -
        MaximumPhysicalIdentifierLength;

    private IpcEndpointName(string logicalLabel, string physicalIdentifier)
    {
        LogicalLabel = logicalLabel;
        PhysicalIdentifier = physicalIdentifier;
    }

    public string LogicalLabel { get; }

    public string PhysicalIdentifier { get; }

    public static IpcEndpointName Create(string logicalLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalLabel);

        Span<byte> entropy = stackalloc byte[EntropyByteCount];
        RandomNumberGenerator.Fill(entropy);
        Span<char> token = stackalloc char[TokenCharacterCount];
        EncodeBase32(entropy, token);
        var physicalIdentifier = PhysicalIdentifierPrefix + token.ToString();
        if (physicalIdentifier.Length > MaximumPhysicalIdentifierLength)
        {
            throw new InvalidOperationException(
                "The generated physical IPC identifier exceeded its fixed policy bound.");
        }

        return new IpcEndpointName(logicalLabel, physicalIdentifier);
    }

    internal static IpcEndpointName CreateDotnetDiagnosticsEmulationForTesting(
        int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The fixed dotnet diagnostics named-pipe fixture is Windows-only.");
        }

        return new IpcEndpointName(
            "dotnet diagnostics attach-stall fixture",
            $"dotnet-diagnostic-{processId}");
    }

    public override string ToString()
    {
        return $"{LogicalLabel} [{PhysicalIdentifier}]";
    }

    private static void EncodeBase32(ReadOnlySpan<byte> entropy, Span<char> token)
    {
        var accumulator = 0u;
        var availableBits = 0;
        var tokenIndex = 0;
        foreach (var value in entropy)
        {
            accumulator = (accumulator << 8) | value;
            availableBits += 8;
            while (availableBits >= 5)
            {
                availableBits -= 5;
                token[tokenIndex++] = TokenAlphabet[checked((int)((accumulator >> availableBits) & 31))];
            }
        }

        if (availableBits != 0 || tokenIndex != token.Length)
        {
            throw new InvalidOperationException("The IPC identifier entropy did not encode exactly.");
        }
    }
}
