using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using DownKyi.ProcessSupervision;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class SupervisorHostProtocolTests
{
    [Fact]
    public void PureProtocolStateRejectsAuthorizationBeforeOwnershipReady()
    {
        var state = new SupervisorProtocolState();

        Assert.Null(state.Advance(SupervisorProtocolKind.AttachOwnership));
        var error = state.Advance(SupervisorProtocolKind.AuthorizeLaunch);

        Assert.NotNull(error);
        Assert.Equal(SupervisorProtocolErrorKind.UnexpectedFrame, error.Kind);
        Assert.Equal(SupervisorProtocolKind.OwnershipReady, error.ExpectedKind);
        Assert.Equal(SupervisorProtocolKind.AuthorizeLaunch, error.ActualKind);
        Assert.False(state.IsComplete);
    }

    [Fact]
    public void PureProtocolStateAcceptsOnlyTheFrozenCompleteOrder()
    {
        var state = new SupervisorProtocolState();
        SupervisorProtocolKind[] orderedKinds =
        [
            SupervisorProtocolKind.AttachOwnership,
            SupervisorProtocolKind.OwnershipReady,
            SupervisorProtocolKind.AuthorizeLaunch,
            SupervisorProtocolKind.TargetStarted,
            SupervisorProtocolKind.TargetExited,
            SupervisorProtocolKind.Finalize,
            SupervisorProtocolKind.Finalized
        ];

        foreach (var kind in orderedKinds)
        {
            Assert.Null(state.Advance(kind));
        }

        Assert.True(state.IsComplete);
        Assert.Equal(
            SupervisorProtocolErrorKind.UnexpectedFrame,
            state.Advance(SupervisorProtocolKind.Finalized)?.Kind);
    }

    [Fact]
    public async Task CoalescedAuthorizationCannotRunBeforeOwnershipReadyIsFlushed()
    {
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization)))
            .ConfigureAwait(true);
        commands.Complete();
        var status = new BlockingFirstFlushStream();
        var capability = new FakeSupervisorCapability();

        var host = SupervisorHost.RunAsync(
            commands,
            status,
            capability,
            TestContext.Current.CancellationToken);
        try
        {
            await status.FirstFlushEntered.WaitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.True(capability.AttachmentObserved.Task.IsCompleted);
            Assert.False(capability.AuthorizationObserved.Task.IsCompleted);

            status.ReleaseFirstFlush();
            var result = await host.ConfigureAwait(true);

            Assert.Equal(SupervisorHostCompletionKind.OwnerChannelClosed, result.Kind);
            Assert.True(capability.AuthorizationObserved.Task.IsCompletedSuccessfully);
            Assert.Equal(1, capability.FailSafeInvocationCount);
        }
        finally
        {
            status.ReleaseFirstFlush();
            commands.Complete();
            _ = await Record.ExceptionAsync(() => host).ConfigureAwait(true);
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using disposable objects complete before disposing",
        Justification = "The finally block completes the command channel, awaits the host task, then asynchronously disposes both streams.")]
    public async Task HostPublishesTargetExitBeforeDispatchingFinalization()
    {
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization)))
            .ConfigureAwait(true);
        var status = new CountingFlushStream();
        var capability = new FakeSupervisorCapability();
        capability.PublishTargetExit(new TargetExited(23));

        var host = SupervisorHost.RunAsync(
            commands,
            status,
            capability,
            TestContext.Current.CancellationToken);
        try
        {
            await status.WaitForFlushCountAsync(3, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.False(capability.FinalizationObserved.Task.IsCompleted);

            await commands.EnqueueAsync(Encode(new FinalizeFrame())).ConfigureAwait(true);
            commands.Complete();
            var result = await host.ConfigureAwait(true);

            Assert.Equal(SupervisorHostCompletionKind.Finalized, result.Kind);
            Assert.True(capability.FinalizationObserved.Task.IsCompletedSuccessfully);
            Assert.Equal(0, capability.FailSafeInvocationCount);
            using var published = new MemoryStream(status.ToArray(), writable: false);
            var frames = await ReadAllFramesAsync(published).ConfigureAwait(true);
            Assert.Collection(
                frames,
                frame => Assert.Equal(
                    Ownership,
                    Assert.IsType<OwnershipReadyFrame>(frame).Ownership),
                frame => Assert.IsType<TargetStartedFrame>(frame),
                frame => Assert.Equal(
                    23,
                    Assert.IsType<TargetExitedFrame>(frame).Exited.ExitCode),
                frame => Assert.IsType<FinalizedFrame>(frame));
        }
        finally
        {
            commands.Complete();
            _ = await Record.ExceptionAsync(() => host).ConfigureAwait(true);
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task OwnerEofInvokesTheInjectedFailSafeExactlyOnce()
    {
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization)))
            .ConfigureAwait(true);
        commands.Complete();
        var status = new MemoryStream();
        var capability = new FakeSupervisorCapability();

        try
        {
            var result = await SupervisorHost.RunAsync(
                    commands,
                    status,
                    capability,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(SupervisorHostCompletionKind.OwnerChannelClosed, result.Kind);
            Assert.Equal(1, capability.FailSafeInvocationCount);
            Assert.False(capability.FinalizationObserved.Task.IsCompleted);
        }
        finally
        {
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FinalizeBeforePublishedTargetExitIsRejectedWithoutDispatch()
    {
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization),
                new FinalizeFrame()))
            .ConfigureAwait(true);
        var status = new MemoryStream();
        var capability = new FakeSupervisorCapability();
        try
        {
            var result = await SupervisorHost.RunAsync(
                    commands,
                    status,
                    capability,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(SupervisorHostCompletionKind.ProtocolRejected, result.Kind);
            Assert.Equal(SupervisorProtocolErrorKind.UnexpectedFrame, result.ProtocolError?.Kind);
            Assert.Equal(SupervisorProtocolKind.TargetExited, result.ProtocolError?.ExpectedKind);
            Assert.Equal(SupervisorProtocolKind.Finalize, result.ProtocolError?.ActualKind);
            Assert.False(capability.FinalizationObserved.Task.IsCompleted);
            Assert.Equal(1, capability.FailSafeInvocationCount);
        }
        finally
        {
            commands.Complete();
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ProtocolRejectionPreservesSupplementalFailSafeFailureExactlyOnce()
    {
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization),
                new FinalizeFrame()))
            .ConfigureAwait(true);
        var status = new MemoryStream();
        var failSafeFailure = new IOException("Injected fail-safe failure.");
        var capability = new FakeSupervisorCapability
        {
            FailSafeFailure = failSafeFailure
        };
        try
        {
            var result = await SupervisorHost.RunAsync(
                    commands,
                    status,
                    capability,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(SupervisorHostCompletionKind.ProtocolRejected, result.Kind);
            Assert.Equal(SupervisorProtocolErrorKind.UnexpectedFrame, result.ProtocolError?.Kind);
            Assert.Same(failSafeFailure, result.FailSafeFailure);
            Assert.Equal(1, capability.FailSafeInvocationCount);
        }
        finally
        {
            commands.Complete();
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task InvalidFrameAfterAuthorizationInvokesFailSafeExactlyOnce()
    {
        var invalidFrame = Encode(new FinalizeFrame());
        invalidFrame[0] = 0;
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization)))
            .ConfigureAwait(true);
        await commands.EnqueueAsync(invalidFrame).ConfigureAwait(true);
        var status = new MemoryStream();
        var capability = new FakeSupervisorCapability();
        try
        {
            var result = await SupervisorHost.RunAsync(
                    commands,
                    status,
                    capability,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(SupervisorHostCompletionKind.ProtocolRejected, result.Kind);
            Assert.Equal(SupervisorProtocolErrorKind.BadMagic, result.ProtocolError?.Kind);
            Assert.Equal(1, capability.FailSafeInvocationCount);
        }
        finally
        {
            commands.Complete();
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task StatusFlushFailureAfterAuthorizationInvokesFailSafeExactlyOnce()
    {
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization)))
            .ConfigureAwait(true);
        var status = new ThrowingFlushStream(failingFlush: 2);
        var capability = new FakeSupervisorCapability();
        try
        {
            _ = await Assert.ThrowsAsync<IOException>(() => SupervisorHost.RunAsync(
                    commands,
                    status,
                    capability,
                    TestContext.Current.CancellationToken))
                .ConfigureAwait(true);

            Assert.True(capability.AuthorizationObserved.Task.IsCompletedSuccessfully);
            Assert.Equal(1, capability.FailSafeInvocationCount);
        }
        finally
        {
            commands.Complete();
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using disposable objects complete before disposing",
        Justification = "The finally block cancels and awaits the host task before asynchronously disposing both streams.")]
    public async Task CancellationAfterAuthorizationCannotCancelTheFailSafe()
    {
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization)))
            .ConfigureAwait(true);
        var status = new MemoryStream();
        var capability = new FakeSupervisorCapability();
        using var cancellation = new CancellationTokenSource();
        var host = SupervisorHost.RunAsync(commands, status, capability, cancellation.Token);
        try
        {
            await capability.AuthorizationObserved.Task.WaitAsync(
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await cancellation.CancelAsync().ConfigureAwait(true);

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host)
                .ConfigureAwait(true);
            Assert.Equal(1, capability.FailSafeInvocationCount);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            commands.Complete();
            _ = await Record.ExceptionAsync(() => host).ConfigureAwait(true);
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task CapabilityExceptionAfterAuthorizationInvokesFailSafeExactlyOnce()
    {
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization)))
            .ConfigureAwait(true);
        var status = new MemoryStream();
        var capability = new FakeSupervisorCapability
        {
            TargetExitFailure = new InvalidOperationException("Injected target-exit failure.")
        };
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => SupervisorHost.RunAsync(
                        commands,
                        status,
                        capability,
                        TestContext.Current.CancellationToken))
                .ConfigureAwait(true);

            Assert.Equal("Injected target-exit failure.", failure.Message);
            Assert.Equal(1, capability.FailSafeInvocationCount);
        }
        finally
        {
            commands.Complete();
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task CapabilityAndFailSafeExceptionsRetainCausalOrderExactlyOnce()
    {
        var commands = new QueuedReadStream();
        await commands.EnqueueAsync(Concat(
                new AttachOwnershipFrame(Attachment),
                new AuthorizeLaunchFrame(Authorization)))
            .ConfigureAwait(true);
        var status = new MemoryStream();
        var executionFailure = new InvalidOperationException("Injected target-exit failure.");
        var failSafeFailure = new IOException("Injected fail-safe failure.");
        var capability = new FakeSupervisorCapability
        {
            TargetExitFailure = executionFailure,
            FailSafeFailure = failSafeFailure
        };
        try
        {
            var failure = await Assert.ThrowsAsync<SupervisorHostExecutionException>(
                    () => SupervisorHost.RunAsync(
                        commands,
                        status,
                        capability,
                        TestContext.Current.CancellationToken))
                .ConfigureAwait(true);

            Assert.Same(executionFailure, failure.ExecutionFailure);
            Assert.Same(executionFailure, failure.InnerException);
            Assert.Same(failSafeFailure, failure.FailSafeFailure);
            Assert.Equal(1, capability.FailSafeInvocationCount);
        }
        finally
        {
            commands.Complete();
            await commands.DisposeAsync().ConfigureAwait(true);
            await status.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static readonly ContainmentAttachment Attachment =
        new(
            ProcessContainmentBackendKind.WindowsJob,
            "containment",
            "membership",
            "owner");

    private static readonly LaunchSpec Authorization = new(
        "dotnet",
        ["fixture.dll"],
        Path.GetTempPath(),
        environment: null,
        closeStandardInput: true);

    private static readonly ProcessOwnershipMetadata Ownership = new(
        ProcessIdentityAuthority.Unspecified,
        ProcessContainmentKind.Unspecified,
        ProcessContainmentStrength.Unspecified,
        ProcessMembershipAuthority.Unspecified,
        Attachment.ContainmentId,
        Attachment.MembershipId,
        Attachment.OwnerLifetimeId,
        OwnershipEstablished: true);

    private static async Task<IReadOnlyList<SupervisorProtocolFrame>> ReadAllFramesAsync(
        Stream stream)
    {
        var frames = new List<SupervisorProtocolFrame>();
        while (true)
        {
            var read = await SupervisorProtocolCodec.ReadAsync(
                    stream,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            if (read is SupervisorProtocolChannelClosed)
            {
                return frames;
            }

            frames.Add(Assert.IsType<SupervisorProtocolFrameRead>(read).Frame);
        }
    }

    private static byte[] Concat(params SupervisorProtocolFrame[] frames)
    {
        return frames.SelectMany(Encode).ToArray();
    }

    private static byte[] Encode(SupervisorProtocolFrame frame)
    {
        var encoded = SupervisorProtocolCodec.Encode(frame);
        Assert.True(encoded.Succeeded, encoded.Error?.Message);
        return Assert.IsType<byte[]>(encoded.Bytes);
    }

    private sealed class FakeSupervisorCapability : ISupervisorHostCapability
    {
        private int _failSafeInvocationCount;
        private readonly TaskCompletionSource<TargetExited> _targetExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AttachmentObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AuthorizationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FinalizationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int FailSafeInvocationCount => Volatile.Read(ref _failSafeInvocationCount);

        internal Exception? TargetExitFailure { get; set; }

        internal Exception? FailSafeFailure { get; set; }

        public ValueTask<ProcessOwnershipMetadata> AttachOwnershipAsync(
            ContainmentAttachment attachment,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Attachment, attachment);
            AttachmentObserved.SetResult();
            return ValueTask.FromResult(Ownership);
        }

        public ValueTask<TargetStarted> AuthorizeLaunchAsync(
            LaunchSpec launchSpec,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Authorization.FileName, launchSpec.FileName);
            AuthorizationObserved.SetResult();
            return ValueTask.FromResult(new TargetStarted(4242));
        }

        public ValueTask<TargetExited> WaitForTargetExitAsync(
            CancellationToken cancellationToken)
        {
            if (TargetExitFailure != null)
            {
                return ValueTask.FromException<TargetExited>(TargetExitFailure);
            }

            return new ValueTask<TargetExited>(_targetExit.Task.WaitAsync(cancellationToken));
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken)
        {
            FinalizationObserved.SetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask FailSafeOwnerLossAsync()
        {
            Interlocked.Increment(ref _failSafeInvocationCount);
            return FailSafeFailure == null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(FailSafeFailure);
        }

        internal void PublishTargetExit(TargetExited targetExited)
        {
            _targetExit.SetResult(targetExited);
        }
    }

    private class CountingFlushStream : MemoryStream
    {
        private readonly object _sync = new();
        private readonly Dictionary<int, TaskCompletionSource> _flushWaiters = [];
        private int _flushCount;

        internal async Task WaitForFlushCountAsync(
            int count,
            CancellationToken cancellationToken)
        {
            Task wait;
            lock (_sync)
            {
                if (_flushCount >= count)
                {
                    return;
                }

                if (!_flushWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _flushWaiters.Add(count, waiter);
                }
                wait = waiter.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _flushCount++;
                foreach (var waiter in _flushWaiters.Where(entry => entry.Key <= _flushCount))
                {
                    waiter.Value.TrySetResult();
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingFirstFlushStream : CountingFlushStream
    {
        private readonly TaskCompletionSource _firstFlushEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstFlush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _flushCount;

        internal Task FirstFlushEntered => _firstFlushEntered.Task;

        internal void ReleaseFirstFlush()
        {
            _releaseFirstFlush.TrySetResult();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _flushCount) == 1)
            {
                _firstFlushEntered.SetResult();
                await _releaseFirstFlush.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await base.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ThrowingFlushStream : MemoryStream
    {
        private readonly int _failingFlush;
        private int _flushCount;

        internal ThrowingFlushStream(int failingFlush)
        {
            _failingFlush = failingFlush;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _flushCount) == _failingFlush)
            {
                return Task.FromException(new IOException("Injected status flush failure."));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class QueuedReadStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal ValueTask EnqueueAsync(byte[] bytes)
        {
            return _chunks.Writer.WriteAsync(bytes);
        }

        internal void Complete()
        {
            _chunks.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current == null || _offset == _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
                if (!_chunks.Reader.TryRead(out _current))
                {
                    continue;
                }
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chunks.Writer.TryComplete();
            }
            base.Dispose(disposing);
        }
    }
}
