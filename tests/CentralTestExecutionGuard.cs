using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace DownKyi.TestInfrastructure;

internal static class CentralTestExecutionGuard
{
    internal const string AssemblyLoadOwnerKey = "DownKyi.CentralTestAssemblyLoadOwner";
    internal const string AssemblyLoadOwnerValue = "DownKyi.AssemblyLifecycleProbe";

    [ModuleInitializer]
    [SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Every repository test assembly is an executable, and this initializer rejects non-central test hosts before discovery.")]
    internal static void RequireInProcessTestHost()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(
                    "DOWNKYI_TEST_MUTATE_CENTRAL_GUARD_BYPASS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(
                AppContext.GetData(AssemblyLoadOwnerKey) as string,
                AssemblyLoadOwnerValue,
                StringComparison.Ordinal))
        {
            return;
        }

        var endpoint = Environment.GetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_ENDPOINT");
        var expectedTokenText = Environment.GetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_TOKEN");
        var legacyPipeHandle = Environment.GetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_PIPE");
        Environment.SetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_ENDPOINT", null);
        Environment.SetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_PIPE", null);
        Environment.SetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_TOKEN", null);
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(expectedTokenText) ||
            !string.IsNullOrWhiteSpace(legacyPipeHandle))
        {
            ThrowUnauthorized();
        }

        byte[] expectedToken;
        try
        {
            expectedToken = Convert.FromBase64String(expectedTokenText);
        }
        catch (FormatException)
        {
            ThrowUnauthorized();
            return;
        }

        const byte protocolVersion = 1;
        const int invocationHashLength = 32;
        const int frameHeaderLength = sizeof(byte) + sizeof(int);
        var authorizationFrame = new byte[
            frameHeaderLength + expectedToken.Length + invocationHashLength];
        using var pipe = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.In,
            PipeOptions.None);
        pipe.Connect();
        var offset = 0;
        while (offset < authorizationFrame.Length)
        {
            var read = pipe.Read(
                authorizationFrame,
                offset,
                authorizationFrame.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var actualInvocationHash = ComputeInvocationHash(Environment.GetCommandLineArgs());
        if (expectedToken.Length != 32 ||
            offset != authorizationFrame.Length ||
            authorizationFrame[0] != protocolVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(
                authorizationFrame.AsSpan(sizeof(byte), sizeof(int))) !=
                expectedToken.Length + invocationHashLength ||
            !CryptographicOperations.FixedTimeEquals(
                authorizationFrame.AsSpan(frameHeaderLength, expectedToken.Length),
                expectedToken) ||
            !CryptographicOperations.FixedTimeEquals(
                authorizationFrame.AsSpan(
                    frameHeaderLength + expectedToken.Length,
                    invocationHashLength),
                actualInvocationHash) ||
            pipe.ReadByte() != -1)
        {
            ThrowUnauthorized();
        }
    }

    private static byte[] ComputeInvocationHash(string[] arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write(arguments.Length);
            foreach (var argument in arguments)
            {
                var bytes = Encoding.UTF8.GetBytes(argument);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        return SHA256.HashData(stream.ToArray());
    }

    [DoesNotReturn]
    private static void ThrowUnauthorized()
    {
        throw new InvalidOperationException(
            $"Repository test assembly '{typeof(CentralTestExecutionGuard).Assembly.GetName().Name}' " +
            "must execute through the central in-process test runner.");
    }
}
