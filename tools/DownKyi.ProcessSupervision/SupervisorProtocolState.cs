namespace DownKyi.ProcessSupervision;

internal sealed class SupervisorProtocolState
{
    private static readonly SupervisorProtocolKind[] OrderedKinds =
    [
        SupervisorProtocolKind.AttachOwnership,
        SupervisorProtocolKind.OwnershipReady,
        SupervisorProtocolKind.AuthorizeLaunch,
        SupervisorProtocolKind.TargetStarted,
        SupervisorProtocolKind.TargetExited,
        SupervisorProtocolKind.Finalize,
        SupervisorProtocolKind.Finalized
    ];

    private int _position;

    internal bool IsComplete => _position == OrderedKinds.Length;

    internal SupervisorProtocolError? Validate(SupervisorProtocolKind actualKind)
    {
        var expectedKind = _position < OrderedKinds.Length
            ? OrderedKinds[_position]
            : (SupervisorProtocolKind?)null;
        if (expectedKind != actualKind)
        {
            return new SupervisorProtocolError(
                SupervisorProtocolErrorKind.UnexpectedFrame,
                "The supervision frame is illegal in the current protocol state.",
                expectedKind,
                actualKind);
        }

        return null;
    }

    internal SupervisorProtocolError? Advance(SupervisorProtocolKind actualKind)
    {
        var validationError = Validate(actualKind);
        if (validationError != null)
        {
            return validationError;
        }

        _position++;
        return null;
    }
}
