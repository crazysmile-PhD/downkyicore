using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

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

        var actualToken = new byte[expectedToken.Length];
        using var pipe = new AnonymousPipeClientStream(PipeDirection.In, pipeHandle);
        var offset = 0;
        while (offset < actualToken.Length)
        {
            var read = pipe.Read(actualToken, offset, actualToken.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (expectedToken.Length != 32 ||
            offset != actualToken.Length ||
            !CryptographicOperations.FixedTimeEquals(actualToken, expectedToken))
        {
            ThrowUnauthorized();
        }
    }

    [DoesNotReturn]
    private static void ThrowUnauthorized()
    {
        throw new InvalidOperationException(
            $"Repository test assembly '{typeof(CentralTestExecutionGuard).Assembly.GetName().Name}' " +
            "must execute through the central in-process test runner.");
    }
}
