using Microsoft.Extensions.Time.Testing;
using OpenDeviceStudio.Control.State;

namespace OpenDeviceStudio.Tests;

public sealed class DeviceSnapshotStoreTests
{
    [Fact]
    public void Newer_push_observation_is_not_overwritten_by_older_poll_result()
    {
        var time = new FakeTimeProvider();
        using var store = new AsyncDisposableAdapter<DeviceSnapshotStore<int>>(
            new DeviceSnapshotStore<int>(time));
        var partition = new DeviceStatePartitionKey("temperature");

        var pollStarted = time.GetTimestamp();
        time.Advance(TimeSpan.FromSeconds(1));
        var eventObserved = time.GetTimestamp();

        var push = store.Value.Apply(new DeviceObservation<int>(
            "device-1",
            partition,
            ConnectionEpoch: 1,
            Source: DeviceObservationSource.Push,
            ObservedTimestamp: eventObserved,
            Value: 25,
            SourceSequence: 2,
            StaleAfter: TimeSpan.FromSeconds(10)));

        var latePoll = store.Value.Apply(new DeviceObservation<int>(
            "device-1",
            partition,
            ConnectionEpoch: 1,
            Source: DeviceObservationSource.Poll,
            ObservedTimestamp: pollStarted,
            Value: 20,
            SourceSequence: 1,
            StaleAfter: TimeSpan.FromSeconds(10)));

        Assert.True(push.Applied);
        Assert.Equal(DeviceObservationApplyStatus.RejectedOlderObservation, latePoll.Status);

        var current = Assert.IsType<DeviceSnapshot<int>>(store.Value.Get("device-1", partition));
        Assert.Equal(25, current.Value);
        Assert.Equal(DeviceObservationSource.Push, current.Source);
        Assert.Equal(push.Snapshot!.SnapshotVersion, current.SnapshotVersion);
    }

    [Fact]
    public void Connection_epoch_invalidates_old_good_snapshot_and_rejects_late_old_epoch()
    {
        var time = new FakeTimeProvider();
        using var store = new AsyncDisposableAdapter<DeviceSnapshotStore<int>>(
            new DeviceSnapshotStore<int>(time));
        var partition = new DeviceStatePartitionKey("position");

        var initial = store.Value.Apply(new DeviceObservation<int>(
            "device-1",
            partition,
            ConnectionEpoch: 1,
            Source: DeviceObservationSource.Poll,
            ObservedTimestamp: time.GetTimestamp(),
            Value: 10));

        var invalidated = Assert.Single(
            store.Value.AdvanceConnectionEpoch("device-1", connectionEpoch: 2));

        var late = store.Value.Apply(new DeviceObservation<int>(
            "device-1",
            partition,
            ConnectionEpoch: 1,
            Source: DeviceObservationSource.Push,
            ObservedTimestamp: time.GetTimestamp(),
            Value: 99));

        Assert.Equal(DeviceSnapshotQuality.Unknown, invalidated.Quality);
        Assert.Equal(2, invalidated.ConnectionEpoch);
        Assert.True(invalidated.SnapshotVersion > initial.Snapshot!.SnapshotVersion);
        Assert.Equal(DeviceObservationApplyStatus.RejectedStaleEpoch, late.Status);

        var current = Assert.IsType<DeviceSnapshot<int>>(store.Value.Get("device-1", partition));
        Assert.Equal(10, current.Value);
        Assert.Equal(2, current.ConnectionEpoch);
        Assert.Equal(DeviceSnapshotQuality.Unknown, current.Quality);
    }

    [Fact]
    public void Old_epoch_cannot_create_first_partition_after_epoch_floor_advanced()
    {
        var time = new FakeTimeProvider();
        using var store = new AsyncDisposableAdapter<DeviceSnapshotStore<int>>(
            new DeviceSnapshotStore<int>(time));
        var partition = new DeviceStatePartitionKey("late-first-partition");

        Assert.Empty(store.Value.AdvanceConnectionEpoch(
            "device-1",
            connectionEpoch: 2,
            qualityReason: "rehydrating"));

        var late = store.Value.Apply(new DeviceObservation<int>(
            "device-1",
            partition,
            ConnectionEpoch: 1,
            Source: DeviceObservationSource.Push,
            ObservedTimestamp: time.GetTimestamp(),
            Value: 99));

        Assert.Equal(DeviceObservationApplyStatus.RejectedStaleEpoch, late.Status);
        Assert.Null(late.Snapshot);
        Assert.Null(store.Value.Get("device-1", partition));
    }

    [Fact]
    public void Freshness_becomes_stale_without_waiting_for_another_poll()
    {
        var time = new FakeTimeProvider();
        using var store = new AsyncDisposableAdapter<DeviceSnapshotStore<double>>(
            new DeviceSnapshotStore<double>(time));
        var partition = new DeviceStatePartitionKey("pressure");

        store.Value.Apply(new DeviceObservation<double>(
            "device-1",
            partition,
            ConnectionEpoch: 3,
            Source: DeviceObservationSource.Poll,
            ObservedTimestamp: time.GetTimestamp(),
            Value: 1.25,
            StaleAfter: TimeSpan.FromSeconds(5)));

        Assert.Equal(
            DeviceSnapshotQuality.Good,
            store.Value.Get("device-1", partition)!.Quality);

        time.Advance(TimeSpan.FromSeconds(6));

        var stale = store.Value.Get("device-1", partition)!;
        Assert.Equal(DeviceSnapshotQuality.Stale, stale.Quality);
        Assert.Equal("freshness_timeout", stale.QualityReason);
    }

    [Fact]
    public async Task Slow_subscription_is_bounded_and_observes_latest_snapshot()
    {
        var time = new FakeTimeProvider();
        await using var store = new DeviceSnapshotStore<int>(time);
        var partition = new DeviceStatePartitionKey("counter");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = store
            .Subscribe("device-1", partition, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var firstMove = enumerator.MoveNextAsync().AsTask();
        store.Apply(new DeviceObservation<int>(
            "device-1", partition, 1, DeviceObservationSource.Push,
            time.GetTimestamp(), 1));

        Assert.True(await firstMove);
        Assert.Equal(1, enumerator.Current.Value);

        store.Apply(new DeviceObservation<int>(
            "device-1", partition, 1, DeviceObservationSource.Push,
            time.GetTimestamp(), 2, SourceSequence: 2));
        store.Apply(new DeviceObservation<int>(
            "device-1", partition, 1, DeviceObservationSource.Push,
            time.GetTimestamp(), 3, SourceSequence: 3));
        store.Apply(new DeviceObservation<int>(
            "device-1", partition, 1, DeviceObservationSource.Push,
            time.GetTimestamp(), 4, SourceSequence: 4));

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(4, enumerator.Current.Value);
    }

    [Fact]
    public void Snapshot_versions_increase_only_for_committed_updates()
    {
        var time = new FakeTimeProvider();
        using var store = new AsyncDisposableAdapter<DeviceSnapshotStore<int>>(
            new DeviceSnapshotStore<int>(time));
        var partition = new DeviceStatePartitionKey("state");

        var first = store.Value.Apply(new DeviceObservation<int>(
            "device-1", partition, 1, DeviceObservationSource.Push,
            time.GetTimestamp(), 1, SourceSequence: 1));

        var duplicate = store.Value.Apply(new DeviceObservation<int>(
            "device-1", partition, 1, DeviceObservationSource.Push,
            time.GetTimestamp(), 2, SourceSequence: 1));

        time.Advance(TimeSpan.FromMilliseconds(1));
        var second = store.Value.Apply(new DeviceObservation<int>(
            "device-1", partition, 1, DeviceObservationSource.Push,
            time.GetTimestamp(), 3, SourceSequence: 2));

        Assert.Equal(DeviceObservationApplyStatus.RejectedOlderObservation, duplicate.Status);
        Assert.Equal(first.Snapshot!.SnapshotVersion + 1, second.Snapshot!.SnapshotVersion);
    }

    private sealed class AsyncDisposableAdapter<T>(T value) : IDisposable
        where T : IAsyncDisposable
    {
        public T Value { get; } = value;

        public void Dispose() =>
            Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
