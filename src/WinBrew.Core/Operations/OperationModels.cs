namespace WinBrew.Core.Operations;

/// <summary>Mutating operation types (A11, schema v1 "operations.type").</summary>
public enum OperationType
{
    Install,
    Upgrade,
    Uninstall,
    Bundle,
    SelfInstall,
    SelfUpdate,
    SelfUninstall,
    RepoAdd,
    RepoRemove,
}

public static class OperationTypeWire
{
    public static string ToWire(OperationType type) => type switch
    {
        OperationType.Install => "install",
        OperationType.Upgrade => "upgrade",
        OperationType.Uninstall => "uninstall",
        OperationType.Bundle => "bundle",
        OperationType.SelfInstall => "selfinstall",
        OperationType.SelfUpdate => "selfupdate",
        OperationType.SelfUninstall => "selfuninstall",
        OperationType.RepoAdd => "repoadd",
        OperationType.RepoRemove => "reporemove",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static bool TryFromWire(string wire, out OperationType type)
    {
        switch (wire)
        {
            case "install": type = OperationType.Install; return true;
            case "upgrade": type = OperationType.Upgrade; return true;
            case "uninstall": type = OperationType.Uninstall; return true;
            case "bundle": type = OperationType.Bundle; return true;
            case "selfinstall": type = OperationType.SelfInstall; return true;
            case "selfupdate": type = OperationType.SelfUpdate; return true;
            case "selfuninstall": type = OperationType.SelfUninstall; return true;
            case "repoadd": type = OperationType.RepoAdd; return true;
            case "reporemove": type = OperationType.RepoRemove; return true;
            default: type = default; return false;
        }
    }
}

/// <summary>
/// Operation state machine (A11):
/// Planned → Downloaded → Verified → Extracted → Validated → Placed →
/// ShimsActivated → StateCommitted → Completed, plus Compensating →
/// RolledBack and RecoveryFailed.
/// </summary>
public enum OperationState
{
    Planned,
    Downloaded,
    Verified,
    Extracted,
    Validated,
    Placed,
    ShimsActivated,
    StateCommitted,
    Completed,
    Compensating,
    RolledBack,
    RecoveryFailed,
}

public static class OperationStateWire
{
    public static string ToWire(OperationState state) => state switch
    {
        OperationState.Planned => "Planned",
        OperationState.Downloaded => "Downloaded",
        OperationState.Verified => "Verified",
        OperationState.Extracted => "Extracted",
        OperationState.Validated => "Validated",
        OperationState.Placed => "Placed",
        OperationState.ShimsActivated => "ShimsActivated",
        OperationState.StateCommitted => "StateCommitted",
        OperationState.Completed => "Completed",
        OperationState.Compensating => "Compensating",
        OperationState.RolledBack => "RolledBack",
        OperationState.RecoveryFailed => "RecoveryFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static bool TryFromWire(string wire, out OperationState state)
    {
        foreach (OperationState s in Enum.GetValues<OperationState>())
        {
            if (ToWire(s) == wire)
            {
                state = s;
                return true;
            }
        }

        state = default;
        return false;
    }

    /// <summary>Terminal states — no further transitions (A11).</summary>
    public static bool IsTerminal(OperationState state) => state is OperationState.Completed
        or OperationState.RolledBack
        or OperationState.RecoveryFailed;
}
