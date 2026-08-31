using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DownKyi.ProcessSupervision;

internal sealed class PlatformSupervisorHostCapability : ISupervisorHostCapability
{
    private IProcessContainmentBackend? _backend;
    private ContainmentAttachment? _attachment;
    private Process? _target;
    private Task _standardOutputPump = Task.CompletedTask;
    private Task _standardErrorPump = Task.CompletedTask;

    public ValueTask<ProcessOwnershipMetadata> AttachOwnershipAsync(
        ContainmentAttachment attachment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_backend != null)
        {
            throw new InvalidOperationException("Containment ownership was already attached.");
        }

        var backend = PlatformProcessContainmentRouter.SelectEstablished(attachment);
        backend.EstablishCurrentProcess(attachment);
        _backend = backend;
        _attachment = attachment;
        return ValueTask.FromResult(CreateEstablishedMetadata(attachment));
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The standard-handle wrappers must remain open for the supervisor process lifetime while target streams are pumped.")]
    public ValueTask<TargetStarted> AuthorizeLaunchAsync(
        LaunchSpec launchSpec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchSpec);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAttached();
        if (_target != null)
        {
            throw new InvalidOperationException("Target launch was already authorized.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launchSpec.FileName,
            WorkingDirectory = launchSpec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = launchSpec.CloseStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in launchSpec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var variable in launchSpec.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        var target = new Process { StartInfo = startInfo };
        try
        {
            if (!target.Start())
            {
                throw new InvalidOperationException("The authorized target did not start.");
            }
            if (launchSpec.CloseStandardInput)
            {
                target.StandardInput.Close();
            }

            _target = target;
            _standardOutputPump = target.StandardOutput.BaseStream.CopyToAsync(
                Console.OpenStandardOutput(),
                cancellationToken);
            _standardErrorPump = target.StandardError.BaseStream.CopyToAsync(
                Console.OpenStandardError(),
                cancellationToken);
            return ValueTask.FromResult(new TargetStarted(target.Id));
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    public async ValueTask<TargetExited> WaitForTargetExitAsync(
        CancellationToken cancellationToken)
    {
        var target = _target ?? throw new InvalidOperationException(
            "Target exit cannot be observed before launch.");
        await target.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _backend!.PrepareCurrentProcessForObservation(_attachment!);
        return new TargetExited(target.ExitCode);
    }

    public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(_standardOutputPump, _standardErrorPump)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        _target?.Dispose();
        _target = null;
    }

    public async ValueTask FailSafeOwnerLossAsync()
    {
        if (_backend != null && _attachment != null)
        {
            _backend.TerminateCurrentProcessTree(_attachment);
        }
        await Task.WhenAll(_standardOutputPump, _standardErrorPump).ConfigureAwait(false);
        _target?.Dispose();
        _target = null;
    }

    private void EnsureAttached()
    {
        if (_backend == null || _attachment == null)
        {
            throw new InvalidOperationException(
                "Target launch cannot be authorized before containment attachment.");
        }
    }

    private static ProcessOwnershipMetadata CreateEstablishedMetadata(
        ContainmentAttachment attachment)
    {
        var authority = attachment.BackendKind switch
        {
            ProcessContainmentBackendKind.WindowsJob => (
                ProcessIdentityAuthority.WindowsProcessHandle,
                ProcessContainmentKind.WindowsJobObject,
                ProcessContainmentStrength.KernelJobTree,
                ProcessMembershipAuthority.WindowsJobAccounting),
            ProcessContainmentBackendKind.LinuxDelegatedCgroup => (
                ProcessIdentityAuthority.DirectChildWait,
                ProcessContainmentKind.LinuxCgroupV2,
                ProcessContainmentStrength.DelegatedCgroupTree,
                ProcessMembershipAuthority.LinuxCgroupV2),
            ProcessContainmentBackendKind.LinuxProcessGroup => (
                ProcessIdentityAuthority.DirectChildWait,
                ProcessContainmentKind.LinuxProcessGroup,
                ProcessContainmentStrength.TrustedChildProcessGroup,
                ProcessMembershipAuthority.LinuxProcessGroupSignal),
            ProcessContainmentBackendKind.MacProcessGroup => (
                ProcessIdentityAuthority.DirectChildWait,
                ProcessContainmentKind.MacOSProcessGroup,
                ProcessContainmentStrength.TrustedChildProcessGroup,
                ProcessMembershipAuthority.MacOSLibprocProcessGroup),
            _ => throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unknown containment backend {(int)attachment.BackendKind}."))
        };

        return new ProcessOwnershipMetadata(
            authority.Item1,
            authority.Item2,
            authority.Item3,
            authority.Item4,
            attachment.ContainmentId,
            attachment.MembershipId,
            attachment.OwnerLifetimeId,
            OwnershipEstablished: true);
    }
}
