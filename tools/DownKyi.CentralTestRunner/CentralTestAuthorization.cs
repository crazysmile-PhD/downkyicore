using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // PowerShell lifecycle compatibility invokes this compiled authorization issuer.

namespace DownKyi.CentralTestRunner;

public sealed class CentralTestAuthorization : IAsyncDisposable, IDisposable
{
    public const string EndpointEnvironmentVariable = "DOWNKYI_CENTRAL_TEST_ENDPOINT";
    public const string TokenEnvironmentVariable = "DOWNKYI_CENTRAL_TEST_TOKEN";
    public const string LegacyPipeEnvironmentVariable = "DOWNKYI_CENTRAL_TEST_PIPE";

    private const byte ProtocolVersion = 1;
    private const int TokenLength = 32;
    private const int InvocationHashLength = 32;

    private readonly IReadOnlyList<string> _arguments;
    private readonly byte[] _token;
    private readonly byte[] _invocationHash;
    private readonly NamedPipeServerStream _authorization;
    private readonly CentralTestAuthorizationMutation _mutation;
    private int _completionState;
    private int _disposed;

    private CentralTestAuthorization(
        IReadOnlyList<string> arguments,
        byte[] token,
        byte[] invocationHash,
        IpcEndpointName endpoint,
        NamedPipeServerStream authorization,
        CentralTestAuthorizationMutation mutation)
    {
        _arguments = arguments;
        _token = token;
        _invocationHash = invocationHash;
        Endpoint = endpoint.PhysicalIdentifier;
        _authorization = authorization;
        _mutation = mutation;
    }

    public string Endpoint { get; }

    public static CentralTestAuthorization Issue(
        IEnumerable<string> arguments,
        string repositoryRoot)
    {
        return IssueCore(arguments, repositoryRoot, CentralTestAuthorizationMutation.None);
    }

    internal static CentralTestAuthorization IssueForTesting(
        IEnumerable<string> arguments,
        string repositoryRoot,
        CentralTestAuthorizationMutation mutation)
    {
        return IssueCore(arguments, repositoryRoot, mutation);
    }

    public void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var executableName = Path.GetFileName(startInfo.FileName);
        var actualArguments = startInfo.ArgumentList.Select(value => value).ToArray();
        if (executableName is not ("dotnet" or "dotnet.exe") ||
            !actualArguments.SequenceEqual(_arguments, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Repository test authorization does not match the complete invocation contract.");
        }

        ApplyEnvironment(startInfo.Environment);
    }

    public void ApplyEnvironment(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        environment[LegacyPipeEnvironmentVariable] = null;
        environment[EndpointEnvironmentVariable] = Endpoint;
        var environmentToken = _mutation == CentralTestAuthorizationMutation.WrongToken
            ? RandomNumberGenerator.GetBytes(TokenLength)
            : _token;
        environment[TokenEnvironmentVariable] = Convert.ToBase64String(environmentToken);
    }

    public async Task CompleteAsync(
        TransitionBudget budget,
        CancellationToken targetExitedToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Repository test process authorization was already completed.");
        }

        using var completion = CancellationTokenSource.CreateLinkedTokenSource(
            targetExitedToken,
            cancellationToken);
        try
        {
            await WaitWithinBudgetAsync(
                    _authorization.WaitForConnectionAsync(completion.Token),
                    budget,
                    "The repository test process did not connect to its authorization endpoint before the operation deadline.",
                    completion.Token)
                .ConfigureAwait(false);

            var frame = CreateFrame();
            var payload = _mutation == CentralTestAuthorizationMutation.Partial
                ? frame.AsMemory(0, frame.Length - 1)
                : frame.AsMemory();
            await WriteWithinBudgetAsync(
                    _authorization,
                    payload,
                    budget,
                    "The repository test authorization payload exceeded the operation deadline.",
                    completion.Token)
                .ConfigureAwait(false);
            if (_mutation == CentralTestAuthorizationMutation.Replay)
            {
                await WriteWithinBudgetAsync(
                        _authorization,
                        frame,
                        budget,
                        "The replayed repository test authorization payload exceeded the operation deadline.",
                        completion.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException failure) when (
            targetExitedToken.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "The owned repository test process exited before authorization completed.",
                failure);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task CloseAfterConnectionForTestingAsync(
        TransitionBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Repository test process authorization was already completed.");
        }

        try
        {
            await WaitWithinBudgetAsync(
                    _authorization.WaitForConnectionAsync(cancellationToken),
                    budget,
                    "The repository test process did not connect to its authorization endpoint before the operation deadline.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _authorization.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _authorization.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static byte[] ComputeInvocationHash(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = arguments.ToArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write(values.Length);
            foreach (var argument in values)
            {
                var bytes = Encoding.UTF8.GetBytes(argument);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        return SHA256.HashData(stream.ToArray());
    }

    private static CentralTestAuthorization IssueCore(
        IEnumerable<string> arguments,
        string repositoryRoot,
        CentralTestAuthorizationMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var immutableArguments = new ReadOnlyCollection<string>(arguments.ToArray());
        if (immutableArguments.Count < 1)
        {
            throw new InvalidOperationException(
                "Authorized repository test execution requires dotnet with a test assembly as its first argument.");
        }

        var requestedAssembly = Path.GetFullPath(immutableArguments[0], repositoryRoot);
        if (!CentralTestPolicy.GetOwnedAssemblyPaths(repositoryRoot).Contains(
                requestedAssembly,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The requested process is not a policy-owned repository test assembly: " +
                Path.GetRelativePath(Path.GetFullPath(repositoryRoot), requestedAssembly)
                    .Replace('\\', '/'));
        }

        var endpoint = IpcEndpointName.Create("CentralTestAuthorization");
        var server = new NamedPipeServerStream(
            endpoint.PhysicalIdentifier,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var hash = ComputeInvocationHash(immutableArguments);
        if (mutation == CentralTestAuthorizationMutation.WrongInvocationHash)
        {
            hash = new byte[InvocationHashLength];
        }

        return new CentralTestAuthorization(
            immutableArguments,
            RandomNumberGenerator.GetBytes(TokenLength),
            hash,
            endpoint,
            server,
            mutation);
    }

    private byte[] CreateFrame()
    {
        var frame = new byte[sizeof(byte) + sizeof(int) + TokenLength + InvocationHashLength];
        frame[0] = ProtocolVersion;
        BinaryPrimitives.WriteInt32LittleEndian(
            frame.AsSpan(sizeof(byte)),
            TokenLength + InvocationHashLength);
        _token.CopyTo(frame.AsSpan(sizeof(byte) + sizeof(int), TokenLength));
        _invocationHash.CopyTo(
            frame.AsSpan(sizeof(byte) + sizeof(int) + TokenLength, InvocationHashLength));
        return frame;
    }

    private static async Task WaitWithinBudgetAsync(
        Task operation,
        TransitionBudget budget,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        var remaining = budget.RemainingOperation;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(timeoutMessage);
        }

        try
        {
            await operation.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException failure)
        {
            throw new TimeoutException(timeoutMessage, failure);
        }
    }

    private static async Task WriteWithinBudgetAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        TransitionBudget budget,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        var remaining = budget.RemainingOperation;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(timeoutMessage);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        try
        {
            await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage, failure);
        }
    }
}
