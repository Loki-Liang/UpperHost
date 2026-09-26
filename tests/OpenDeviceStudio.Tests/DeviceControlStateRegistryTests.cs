using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.Control.State;

namespace OpenDeviceStudio.Tests;

public sealed class DeviceControlStateRegistryTests
{
    [Fact]
    public async Task Rehydrate_invalidates_snapshots_and_requires_all_requirements_before_ready()
    {
        await using var store = new DeviceSnapshotStore<int>();
        var partition = new DeviceStatePartitionKey("position");
        store.Apply(new DeviceObservation<int>(
            "device-1",
            partition,
            0,
            DeviceObservationSource.Poll,
            TimeProvider.System.GetTimestamp(),
            10));

        var registry = new DeviceControlStateRegistry([store]);
        var started = registry.BeginRehydrate(
            "device-1",
            ["state", "parameters"]);

        Assert.Equal(1, started.ConnectionEpoch);
        Assert.Equal(DeviceControlReadinessState.Rehydrating, started.State);
        Assert.Equal(2, started.PendingRequirements.Count);

        var invalidated = store.Get("device-1", partition);
        Assert.NotNull(invalidated);
        Assert.Equal(1, invalidated.ConnectionEpoch);
        Assert.Equal(DeviceSnapshotQuality.Unknown, invalidated.Quality);

        var partial = registry.MarkRequirementSatisfied(
            "device-1",
            started.ConnectionEpoch,
            "state");
        Assert.False(partial.IsReady);

        var ready = registry.MarkRequirementSatisfied(
            "device-1",
            started.ConnectionEpoch,
            "parameters");
        Assert.True(ready.IsReady);
        Assert.Equal(DeviceControlReadinessState.Ready, ready.State);
    }

    [Fact]
    public void Old_epoch_completion_cannot_make_new_epoch_ready()
    {
        var registry = new DeviceControlStateRegistry();

        var first = registry.BeginRehydrate("device-1", ["state"]);
        var second = registry.BeginRehydrate("device-1", ["state"]);

        var staleCompletion = registry.MarkRequirementSatisfied(
            "device-1",
            first.ConnectionEpoch,
            "state");

        Assert.Equal(second.ConnectionEpoch, staleCompletion.ConnectionEpoch);
        Assert.Equal(DeviceControlReadinessState.Rehydrating, staleCompletion.State);

        var ready = registry.MarkRequirementSatisfied(
            "device-1",
            second.ConnectionEpoch,
            "state");
        Assert.True(ready.IsReady);
    }

    [Fact]
    public void Required_failure_is_diagnostic_and_blocks_ready()
    {
        var registry = new DeviceControlStateRegistry();
        var started = registry.BeginRehydrate(
            "device-1",
            ["identity", "parameters"]);

        var faulted = registry.MarkRequirementFailed(
            "device-1",
            started.ConnectionEpoch,
            "identity",
            "device_identity_mismatch");

        Assert.Equal(DeviceControlReadinessState.Faulted, faulted.State);
        Assert.Equal("device_identity_mismatch", faulted.FailedRequirements["identity"]);
        Assert.False(faulted.IsReady);
    }

    [Fact]
    public async Task Disconnect_advances_epoch_and_rejects_late_observation()
    {
        await using var store = new DeviceSnapshotStore<int>();
        var partition = new DeviceStatePartitionKey("status");
        var registry = new DeviceControlStateRegistry([store]);

        var readyEpoch = registry.BeginRehydrate("device-1");
        store.Apply(new DeviceObservation<int>(
            "device-1",
            partition,
            readyEpoch.ConnectionEpoch,
            DeviceObservationSource.Push,
            TimeProvider.System.GetTimestamp(),
            1));

        var disconnected = registry.MarkDisconnected("device-1");

        var late = store.Apply(new DeviceObservation<int>(
            "device-1",
            partition,
            readyEpoch.ConnectionEpoch,
            DeviceObservationSource.Push,
            TimeProvider.System.GetTimestamp(),
            99));

        Assert.Equal(DeviceControlReadinessState.Offline, disconnected.State);
        Assert.True(disconnected.ConnectionEpoch > readyEpoch.ConnectionEpoch);
        Assert.Equal(DeviceObservationApplyStatus.RejectedStaleEpoch, late.Status);
        Assert.Equal(DeviceSnapshotQuality.Unknown, store.Get("device-1", partition)!.Quality);
    }

    [Fact]
    public void Epoch_validator_uses_canonical_device_resource()
    {
        var registry = new DeviceControlStateRegistry();
        var current = registry.BeginRehydrate("device-1");

        Assert.True(registry.IsCurrent(
            current.ConnectionEpoch,
            [new CommandResourceKey("device", "device-1")]));

        registry.BeginRehydrate("device-1");

        Assert.False(registry.IsCurrent(
            current.ConnectionEpoch,
            [new CommandResourceKey("device", "device-1")]));
    }
}
