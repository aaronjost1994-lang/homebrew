using WinBrew.Application.Ports;
using WinBrew.Core.Operations;

namespace WinBrew.UnitTests;

public class FaultCheckpointsTests
{
    [Fact]
    public void Registry_IsAuthoritative_UnknownNamesThrow() // A11: tests enumerate, nothing hardcodes
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FaultCheckpoints.EnsureRegistered("download.started"));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void OperationTypeWire_RoundTrips()
    {
        foreach (OperationType type in Enum.GetValues<OperationType>())
        {
            var wire = OperationTypeWire.ToWire(type);
            Assert.True(OperationTypeWire.TryFromWire(wire, out var back));
            Assert.Equal(type, back);
        }

        Assert.False(OperationTypeWire.TryFromWire("mystery", out _));
    }

    [Fact]
    public void OperationStateWire_RoundTrips_AndDetectsTerminal()
    {
        foreach (OperationState state in Enum.GetValues<OperationState>())
        {
            var wire = OperationStateWire.ToWire(state);
            Assert.True(OperationStateWire.TryFromWire(wire, out var back));
            Assert.Equal(state, back);
        }

        Assert.True(OperationStateWire.IsTerminal(OperationState.Completed));
        Assert.True(OperationStateWire.IsTerminal(OperationState.RolledBack));
        Assert.True(OperationStateWire.IsTerminal(OperationState.RecoveryFailed));
        Assert.False(OperationStateWire.IsTerminal(OperationState.Planned));
        Assert.False(OperationStateWire.IsTerminal(OperationState.Compensating));
    }

    [Fact]
    public async Task NoOpFaultInjector_DoesNothing() // production seam (A11)
    {
        await NoOpFaultInjector.Instance.CheckpointAsync("anything", CancellationToken.None); // no throw, no FailFast
    }
}
