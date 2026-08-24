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
                AppContext.GetData(AssemblyLoadOwnerKey) as string,
                AssemblyLoadOwnerValue,
                StringComparison.Ordinal))
        {
            return;
        }

        var pipeHandle = Environment.GetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_PIPE");
        var expectedTokenText = Environment.GetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_TOKEN");
        Environment.SetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_PIPE", null);
        Environment.SetEnvironmentVariable("DOWNKYI_CENTRAL_TEST_TOKEN", null);
        if (string.IsNullOrWhiteSpace(pipeHandle) || string.IsNullOrWhiteSpace(expectedTokenText))
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

        const int invocationHashLength = 32;
        var authorizationPayload = new byte[expectedToken.Length + invocationHashLength];
        using var pipe = new AnonymousPipeClientStream(PipeDirection.In, pipeHandle);
        var offset = 0;
        while (offset < authorizationPayload.Length)
        {
            var read = pipe.Read(
                authorizationPayload,
                offset,
                authorizationPayload.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var actualInvocationHash = ComputeInvocationHash(Environment.GetCommandLineArgs());
        if (expectedToken.Length != 32 ||
            offset != authorizationPayload.Length ||
            !CryptographicOperations.FixedTimeEquals(
                authorizationPayload.AsSpan(0, expectedToken.Length),
                expectedToken) ||
            !CryptographicOperations.FixedTimeEquals(
                authorizationPayload.AsSpan(expectedToken.Length, invocationHashLength),
                actualInvocationHash))
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
