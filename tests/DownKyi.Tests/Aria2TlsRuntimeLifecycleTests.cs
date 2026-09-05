using DownKyi.Core.Aria2cNet.Client;
using DownKyi.TestInfrastructure;

namespace DownKyi.Tests;

public sealed class Aria2TlsRuntimeLifecycleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task StartupFailureRollsBackEveryPreviouslyAcquiredStage(int failureIndex)
    {
        var acquired = new List<int>();
        var rolledBack = new List<int>();
        var primary = new TestStageException($"startup-{failureIndex}");
        var steps = Enumerable.Range(0, 7)
            .Select(index => new Aria2TlsStartupStep(
                $"stage-{index}",
                _ =>
                {
                    if (index == failureIndex)
                    {
                        return Task.FromException(primary);
                    }

                    acquired.Add(index);
                    return Task.CompletedTask;
                },
                () =>
                {
                    rolledBack.Add(index);
                    return Task.CompletedTask;
                }))
            .ToArray();

        var exception = await Record.ExceptionAsync(() =>
            Aria2TlsRuntimeStartup.RunAsync(
                steps,
                TestContext.Current.CancellationToken));

        Assert.Same(primary, exception);
        Assert.Equal(Enumerable.Range(0, failureIndex), acquired);
        Assert.Equal(
            Enumerable.Range(0, failureIndex).Reverse(),
            rolledBack);
    }

    [Fact]
    public async Task StartupPrimaryRemainsFirstWhenEveryRollbackFails()
    {
        var rollbackOrder = new List<string>();
        var primary = new TestStageException("startup-primary");
        var steps = new[]
        {
            CreateFailingRollbackStep("working-directory", rollbackOrder),
            CreateFailingRollbackStep("trusted-root", rollbackOrder),
            new Aria2TlsStartupStep(
                "process",
                _ => Task.FromException(primary))
        };

        var exception = await Assert.ThrowsAsync<Aria2TlsMultipleFailuresException>(() =>
            Aria2TlsRuntimeStartup.RunAsync(
                steps,
                TestContext.Current.CancellationToken));

        Assert.Same(primary, exception.PrimaryFailure.Exception);
        Assert.Equal(
            [
                "runtime-startup/process",
                "runtime-startup-rollback/trusted-root",
                "runtime-startup-rollback/working-directory"
            ],
            exception.Failures.Select(failure => failure.Stage));
        Assert.Equal(["trusted-root", "working-directory"], rollbackOrder);
    }

    [Fact]
    public async Task PartialAcquisitionFailureRunsItsOwnRollbackAndPreservesPrimary()
    {
        var primary = new TestStageException("restrict-secret");
        var rollbackFailure = new TestStageException("delete-secret");
        var rollbackCalled = false;

        var exception = await Assert.ThrowsAsync<Aria2TlsMultipleFailuresException>(() =>
            Aria2TlsRuntimeStartup.AcquireWithPartialRollbackAsync(
                "rpc-secret",
                _ => Task.FromException(primary),
                () =>
                {
                    rollbackCalled = true;
                    return Task.FromException(rollbackFailure);
                },
                TestContext.Current.CancellationToken));

        Assert.True(rollbackCalled);
        Assert.Equal(
            ["runtime-startup/rpc-secret", "runtime-startup-rollback/rpc-secret"],
            exception.Failures.Select(failure => failure.Stage));
        Assert.Same(primary, exception.PrimaryFailure.Exception);
        Assert.Same(rollbackFailure, exception.Failures[1].Exception);
    }

    [Fact]
    public async Task FailureBeforeProcessCreationRemovesTheWorkingDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-tls-startup-{Guid.NewGuid():N}");
        var primary = new TestStageException("trust-file-write");
        var steps = new[]
        {
            new Aria2TlsStartupStep(
                "working-directory",
                _ =>
                {
                    Directory.CreateDirectory(directory);
                    return Task.CompletedTask;
                },
                () =>
                {
                    Directory.Delete(directory, recursive: true);
                    return Task.CompletedTask;
                }),
            new Aria2TlsStartupStep(
                "trusted-root-files",
                _ => Task.FromException(primary))
        };

        var exception = await Record.ExceptionAsync(() =>
            Aria2TlsRuntimeStartup.RunAsync(
                steps,
                TestContext.Current.CancellationToken));

        Assert.Same(primary, exception);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task GracefulRpcShutdownWaitsWithoutKillingTheProcess()
    {
        var process = new FakeProcessHandle();
        process.WaitBehaviors.Enqueue(cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Exited = true;
            return Task.CompletedTask;
        });
        var lifetime = CreateProcessLifetime(process, () => Task.CompletedTask);
        var failures = new Aria2TlsFailureCollector();

        await lifetime.CleanupAsync(failures);

        Assert.Empty(failures.Failures);
        Assert.Equal(1, process.WaitCount);
        Assert.False(process.KillCalled);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RpcShutdownFailureStillKillsAndBoundedlyReapsTheProcess()
    {
        var process = CreateTimeoutThenExitProcess();
        var shutdownFailure = new TestStageException("rpc-shutdown");
        var lifetime = CreateProcessLifetime(
            process,
            () => Task.FromException(shutdownFailure));
        var failures = new Aria2TlsFailureCollector();

        await lifetime.CleanupAsync(failures);

        var failure = Assert.Single(failures.Failures);
        Assert.Equal("runtime-disposal", failure.Stage);
        Assert.Same(shutdownFailure, failure.Exception);
        Assert.True(process.KillCalled);
        Assert.Equal(2, process.WaitCount);
        Assert.All(process.ObservedWaitTokens, token => Assert.True(token.CanBeCanceled));
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RpcShutdownTimeoutStillKillsAndBoundedlyReapsTheProcess()
    {
        var process = CreateTimeoutThenExitProcess();
        var shutdownCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = CreateProcessLifetime(
            process,
            () => shutdownCompletion.Task);
        var failures = new Aria2TlsFailureCollector();

        await lifetime.CleanupAsync(failures);

        var failure = Assert.Single(failures.Failures);
        Assert.Equal("runtime-disposal", failure.Stage);
        Assert.IsType<TimeoutException>(failure.Exception);
        Assert.True(process.KillCalled);
        Assert.Equal(2, process.WaitCount);
        Assert.All(process.ObservedWaitTokens, token => Assert.True(token.CanBeCanceled));
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task TimedOutRpcShutdownLateFailureIsObservedAfterReap()
    {
        var process = CreateTimeoutThenExitProcess();
        var lateFailure = new TestStageException("late RPC shutdown");
        var shutdownCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnKill = () => shutdownCompletion.SetException(lateFailure);
        var lifetime = CreateProcessLifetime(
            process,
            () => shutdownCompletion.Task);
        var failures = new Aria2TlsFailureCollector();

        await lifetime.CleanupAsync(failures);

        Assert.Equal(
            ["runtime-disposal", "runtime-disposal/late-completion"],
            failures.Failures.Select(failure => failure.Stage));
        Assert.IsType<TimeoutException>(failures.Failures[0].Exception);
        Assert.Same(lateFailure, failures.Failures[1].Exception);
        Assert.True(process.KillCalled);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task KillAndReapFailuresDoNotPreventDrainOrProcessDisposal()
    {
        var process = new FakeProcessHandle
        {
            KillFailure = new TestStageException("kill")
        };
        process.WaitBehaviors.Enqueue(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        process.WaitBehaviors.Enqueue(_ => Task.FromException(
            new TestStageException("reap")));
        var stdoutFailure = new TestStageException("stdout");
        var stderrObserved = false;
        var lifetime = CreateProcessLifetime(
            process,
            () => Task.CompletedTask,
            Task.FromException<string>(stdoutFailure),
            ObserveStderrAsync());
        var failures = new Aria2TlsFailureCollector();

        await lifetime.CleanupAsync(failures);

        Assert.Equal(
            ["process-kill", "process-reap", "stdout-drain"],
            failures.Failures.Select(failure => failure.Stage));
        Assert.True(stderrObserved);
        Assert.True(process.Disposed);
        Assert.All(process.ObservedWaitTokens, token => Assert.True(token.CanBeCanceled));

        async Task<string> ObserveStderrAsync()
        {
            await Task.Yield();
            stderrObserved = true;
            return "observed";
        }
    }

    [Fact]
    public async Task StdoutAndStderrFailuresAreBothObservedInStableOrder()
    {
        var process = new FakeProcessHandle { Exited = true };
        var stdoutFailure = new TestStageException("stdout");
        var stderrFailure = new TestStageException("stderr");
        var lifetime = CreateProcessLifetime(
            process,
            () => Task.CompletedTask,
            Task.FromException<string>(stdoutFailure),
            Task.FromException<string>(stderrFailure));
        var failures = new Aria2TlsFailureCollector();

        await lifetime.CleanupAsync(failures);

        Assert.Equal(
            ["stdout-drain", "stderr-drain"],
            failures.Failures.Select(failure => failure.Stage));
        Assert.Same(stdoutFailure, failures.Failures[0].Exception);
        Assert.Same(stderrFailure, failures.Failures[1].Exception);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelCleanupOwnedOutputDrain()
    {
        using var callerCancellation = new CancellationTokenSource();
        await callerCancellation.CancelAsync();
        using var process = new FakeProcessHandle { Exited = true };
        var stdoutObserved = false;
        var stderrObserved = false;
        var lifetime = CreateProcessLifetime(
            process,
            () => Task.CompletedTask,
            ObserveAsync(() => stdoutObserved = true),
            ObserveAsync(() => stderrObserved = true));
        await using var lifetimeScope = lifetime.ConfigureAwait(true);
        var failures = new Aria2TlsFailureCollector();

        await lifetime.CleanupAsync(failures);

        Assert.True(callerCancellation.IsCancellationRequested);
        Assert.True(stdoutObserved);
        Assert.True(stderrObserved);
        Assert.Empty(failures.Failures);

        static async Task<string> ObserveAsync(Action observed)
        {
            await Task.Yield();
            observed();
            return "observed";
        }
    }

    [Fact]
    public async Task RuntimeAttemptsDirectoryCleanupAfterTrustedRootFailure()
    {
        var process = new FakeProcessHandle { Exited = true };
        var lifetime = CreateProcessLifetime(process, () => Task.CompletedTask);
        await using var lifetimeScope = lifetime.ConfigureAwait(true);
        var rootFailure = new TestStageException("root");
        var filesystemFailure = new TestStageException("filesystem");
        var root = new FakeTrustedRoot(rootFailure);
        await using var rootScope = root.ConfigureAwait(true);
        var directoryCalled = false;
        var client = new AriaClient(
            "http://localhost",
            6800,
            "test-token",
            (_, _) => Task.FromResult<string?>(null));
        var caught = await Record.ExceptionAsync(async () =>
        {
            var runtime = new Aria2TlsTestRuntime(
                lifetime,
                client,
                root,
                "test-directory",
                "test-version",
                "test-hash",
                _ =>
                {
                    directoryCalled = true;
                    throw filesystemFailure;
                });
            await using var runtimeScope = runtime.ConfigureAwait(true);
        }).ConfigureAwait(true);
        var exception = Assert.IsType<Aria2TlsMultipleFailuresException>(caught);

        Assert.Equal(
            ["trusted-root", "filesystem"],
            exception.Failures.Select(failure => failure.Stage));
        Assert.Same(rootFailure, exception.Failures[0].Exception);
        Assert.Same(filesystemFailure, exception.Failures[1].Exception);
        Assert.True(root.Disposed);
        Assert.True(directoryCalled);
    }

    [Fact]
    public async Task LinuxTrustedRootInstallRollbackPreservesEveryFailure()
    {
        using var authority = new TestCertificateAuthority("DownKyi TLS Root Test");
        var commands = new List<string>();
        var primary = new TestStageException("install-update");
        var removeFailure = new TestStageException("rollback-remove");
        var updateFailure = new TestStageException("rollback-update");
        var updateCount = 0;

        var exception = await Assert.ThrowsAsync<Aria2TlsMultipleFailuresException>(() =>
            TrustedRootScope.InstallAsync(
                authority.RootCertificate,
                "root.pem",
                "root.cer",
                Aria2TlsHostPlatform.Linux,
                RunCommandAsync,
                _ => throw new InvalidOperationException("Windows registration was not expected."),
                TestContext.Current.CancellationToken));

        Assert.Equal(4, commands.Count);
        Assert.Equal(
            [
                "trusted-root-install",
                "trusted-root-install-rollback/remove",
                "trusted-root-install-rollback/update"
            ],
            exception.Failures.Select(failure => failure.Stage));
        Assert.Same(primary, exception.Failures[0].Exception);
        Assert.Same(removeFailure, exception.Failures[1].Exception);
        Assert.Same(updateFailure, exception.Failures[2].Exception);

        Task RunCommandAsync(
            string _,
            IReadOnlyList<string> arguments,
            CancellationToken __)
        {
            commands.Add(string.Join(' ', arguments));
            if (arguments.Contains("rm", StringComparer.Ordinal))
            {
                return Task.FromException(removeFailure);
            }

            if (arguments.Contains("update-ca-certificates", StringComparer.Ordinal))
            {
                updateCount++;
                return Task.FromException(updateCount == 1 ? primary : updateFailure);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LinuxTrustedRootCopyFailureStillRunsCompleteRollback()
    {
        using var authority = new TestCertificateAuthority("DownKyi TLS Root Copy Test");
        var primary = new IOException("partial certificate copy");
        var commands = new List<string>();
        var cleanupTokens = new List<CancellationToken>();

        var actual = await Record.ExceptionAsync(() => TrustedRootScope.InstallAsync(
            authority.RootCertificate,
            "root.pem",
            "root.cer",
            Aria2TlsHostPlatform.Linux,
            RunCommandAsync,
            _ => throw new InvalidOperationException("Windows registration was not expected."),
            TestContext.Current.CancellationToken));

        Assert.Same(primary, actual);
        Assert.Equal(3, commands.Count);
        Assert.Contains(" install ", $" {commands[0]} ", StringComparison.Ordinal);
        Assert.Contains(" rm ", $" {commands[1]} ", StringComparison.Ordinal);
        Assert.Contains("update-ca-certificates", commands[2], StringComparison.Ordinal);
        Assert.All(cleanupTokens, token => Assert.True(token.CanBeCanceled));

        Task RunCommandAsync(
            string _,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            commands.Add(string.Join(' ', arguments));
            if (arguments.Contains("install", StringComparer.Ordinal))
            {
                return Task.FromException(primary);
            }

            cleanupTokens.Add(cancellationToken);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LinuxTrustedRootRemovalAttemptsUpdateAfterRemoveFailure()
    {
        using var authority = new TestCertificateAuthority("DownKyi TLS Root Removal Test");
        var removeFailure = new TestStageException("remove");
        var updateFailure = new TestStageException("update");
        var removalMode = false;
        var calls = new List<string>();
        var cleanupTokens = new List<CancellationToken>();
        var scope = await TrustedRootScope.InstallAsync(
            authority.RootCertificate,
            "root.pem",
            "root.cer",
            Aria2TlsHostPlatform.Linux,
            RunCommandAsync,
            _ => throw new InvalidOperationException("Windows registration was not expected."),
            TestContext.Current.CancellationToken);
        removalMode = true;

        var exception = await Assert.ThrowsAsync<Aria2TlsMultipleFailuresException>(
            () => scope.DisposeAsync().AsTask());

        Assert.Equal(
            ["trusted-root-remove/linux-remove", "trusted-root-remove/linux-update"],
            exception.Failures.Select(failure => failure.Stage));
        Assert.Equal(2, calls.Count);
        Assert.All(cleanupTokens, token => Assert.True(token.CanBeCanceled));

        Task RunCommandAsync(
            string _,
            IReadOnlyList<string> arguments,
            CancellationToken __)
        {
            if (!removalMode)
            {
                return Task.CompletedTask;
            }

            calls.Add(string.Join(' ', arguments));
            cleanupTokens.Add(__);
            return Task.FromException(
                arguments.Contains("rm", StringComparer.Ordinal)
                    ? removeFailure
                    : updateFailure);
        }
    }

    [Fact]
    public async Task MacTrustedRootPartialInstallRollsBackAndPreservesPrimary()
    {
        using var authority = new TestCertificateAuthority("DownKyi TLS Mac Root Test");
        var primary = new TestStageException("macos-add");
        var rollback = new TestStageException("macos-delete");
        var commands = new List<string>();

        var exception = await Assert.ThrowsAsync<Aria2TlsMultipleFailuresException>(() =>
            TrustedRootScope.InstallAsync(
                authority.RootCertificate,
                "root.pem",
                "root.cer",
                Aria2TlsHostPlatform.MacOS,
                RunCommandAsync,
                _ => throw new InvalidOperationException("Windows registration was not expected."),
                TestContext.Current.CancellationToken));

        Assert.Equal(2, commands.Count);
        Assert.Contains("add-trusted-cert", commands[0], StringComparison.Ordinal);
        Assert.Contains("delete-certificate", commands[1], StringComparison.Ordinal);
        Assert.Equal(
            ["trusted-root-install/macos-add", "trusted-root-install-rollback/macos-delete"],
            exception.Failures.Select(failure => failure.Stage));
        Assert.Same(primary, exception.PrimaryFailure.Exception);
        Assert.Same(rollback, exception.Failures[1].Exception);

        Task RunCommandAsync(
            string _,
            IReadOnlyList<string> arguments,
            CancellationToken __)
        {
            commands.Add(string.Join(' ', arguments));
            return Task.FromException(commands.Count == 1 ? primary : rollback);
        }
    }

    [Fact]
    public void WindowsTrustedRootRemovalObservesDeleteAndCloseFailures()
    {
        var deleteCalled = false;
        var closeCalled = false;
        using var registration = WindowsTrustedRootRegistration.CreateForTest(
            _ =>
            {
                deleteCalled = true;
                return false;
            },
            _ =>
            {
                closeCalled = true;
                return false;
            });

        var exception = Assert.Throws<Aria2TlsMultipleFailuresException>(registration.Dispose);

        Assert.Equal(
            [
                "trusted-root-remove/windows-delete",
                "trusted-root-remove/windows-close"
            ],
            exception.Failures.Select(failure => failure.Stage));
        Assert.True(deleteCalled);
        Assert.True(closeCalled);
    }

    private static Aria2TlsStartupStep CreateFailingRollbackStep(
        string name,
        List<string> rollbackOrder)
    {
        return new Aria2TlsStartupStep(
            name,
            _ => Task.CompletedTask,
            () =>
            {
                rollbackOrder.Add(name);
                return Task.FromException(new TestStageException($"rollback-{name}"));
            });
    }

    private static FakeProcessHandle CreateTimeoutThenExitProcess()
    {
        var process = new FakeProcessHandle();
        process.WaitBehaviors.Enqueue(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        process.WaitBehaviors.Enqueue(_ =>
        {
            process.Exited = true;
            return Task.CompletedTask;
        });
        return process;
    }

    private static Aria2TlsProcessLifetime CreateProcessLifetime(
        FakeProcessHandle process,
        Func<Task> forceShutdown,
        Task<string>? standardOutput = null,
        Task<string>? standardError = null)
    {
        return new Aria2TlsProcessLifetime(
            process,
            standardOutput ?? Task.FromResult(string.Empty),
            standardError ?? Task.FromResult(string.Empty),
            new CancellationTokenSource(),
            forceShutdown,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20));
    }

    private sealed class FakeProcessHandle : IAria2TlsProcessHandle
    {
        public Queue<Func<CancellationToken, Task>> WaitBehaviors { get; } = new();

        public List<CancellationToken> ObservedWaitTokens { get; } = [];

        public bool Disposed { get; private set; }

        public bool Exited { get; set; }

        public bool KillCalled { get; private set; }

        public Exception? KillFailure { get; init; }

        public Action? OnKill { get; set; }

        public int WaitCount { get; private set; }

        public bool HasExited => Exited;

        public void KillEntireTree()
        {
            KillCalled = true;
            OnKill?.Invoke();
            if (KillFailure != null)
            {
                throw KillFailure;
            }
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            ObservedWaitTokens.Add(cancellationToken);
            if (WaitBehaviors.Count == 0)
            {
                Exited = true;
                return;
            }

            await WaitBehaviors.Dequeue()(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class FakeTrustedRoot(Exception failure) : IAria2TlsTrustedRoot
    {
        public string Source => "fake-root";

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            if (Disposed)
            {
                return ValueTask.CompletedTask;
            }

            Disposed = true;
            return ValueTask.FromException(failure);
        }
    }

    private sealed class TestStageException : Exception
    {
        public TestStageException()
        {
        }

        public TestStageException(string message)
            : base(message)
        {
        }

        public TestStageException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
