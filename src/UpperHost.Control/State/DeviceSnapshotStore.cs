using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace UpperHost.Control.State;

public enum DeviceSnapshotQuality
{
    Unknown,
    Good,
    Stale,
    Invalid,
    Faulted
}

public enum DeviceObservationSource
{
    Poll,
    Push,
    ParameterReadback,
    Command,
    Rehydrate
}

public enum DeviceObservationApplyStatus
{
    Applied,
    RejectedStaleEpoch,
    RejectedOlderObservation
}

public readonly record struct DeviceStatePartitionKey(string Value)
{
    public override string ToString() => Value;
}

public sealed record DeviceObservation<TState>(
    string DeviceId,
    DeviceStatePartitionKey Partition,
    long ConnectionEpoch,
    DeviceObservationSource Source,
    long ObservedTimestamp,
    TState Value,
    DeviceSnapshotQuality Quality = DeviceSnapshotQuality.Good,
    long? SourceSequence = null,
    DateTimeOffset? ObservedAtUtc = null,
    string? QualityReason = null,
    TimeSpan? StaleAfter = null);

public sealed record DeviceSnapshot<TState>(
    string DeviceId,
    DeviceStatePartitionKey Partition,
    long SnapshotVersion,
    long ConnectionEpoch,
    DeviceObservationSource Source,
    long ObservedTimestamp,
    TState Value,
    DeviceSnapshotQuality Quality,
    long? SourceSequence,
    DateTimeOffset? ObservedAtUtc,
    string? QualityReason,
    TimeSpan? StaleAfter);

public sealed record DeviceObservationApplyResult<TState>(
    DeviceObservationApplyStatus Status,
    DeviceSnapshot<TState>? Snapshot)
{
    public bool Applied => Status == DeviceObservationApplyStatus.Applied;
}

/// <summary>
/// Per-state-type authoritative reducer for immutable device state fragments.
/// All producers (poll, push, readback, command completion and rehydrate)
/// publish observations through this store instead of mutating UI/device caches.
/// </summary>
public sealed class DeviceSnapshotStore<TState> : IAsyncDisposable
{
    private readonly ConcurrentDictionary<EntryKey, Entry> _entries = new();
    private readonly TimeProvider _timeProvider;
    private long _snapshotVersion;
    private int _disposed;

    public DeviceSnapshotStore(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public DeviceObservationApplyResult<TState> Apply(DeviceObservation<TState> observation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Validate(observation);

        var key = new EntryKey(observation.DeviceId, observation.Partition.Value);
        var entry = _entries.GetOrAdd(key, static _ => new Entry());

        DeviceSnapshot<TState> snapshot;
        Channel<DeviceSnapshot<TState>>[] subscribers;

        lock (entry.Gate)
        {
            if (entry.Current is { } current)
            {
                if (observation.ConnectionEpoch < current.ConnectionEpoch)
                {
                    return new DeviceObservationApplyResult<TState>(
                        DeviceObservationApplyStatus.RejectedStaleEpoch,
                        ApplyFreshness(current));
                }

                if (observation.ConnectionEpoch == current.ConnectionEpoch &&
                    IsOlder(observation, current))
                {
                    return new DeviceObservationApplyResult<TState>(
                        DeviceObservationApplyStatus.RejectedOlderObservation,
                        ApplyFreshness(current));
                }
            }

            snapshot = new DeviceSnapshot<TState>(
                observation.DeviceId,
                observation.Partition,
                Interlocked.Increment(ref _snapshotVersion),
                observation.ConnectionEpoch,
                observation.Source,
                observation.ObservedTimestamp,
                observation.Value,
                observation.Quality,
                observation.SourceSequence,
                observation.ObservedAtUtc,
                observation.QualityReason,
                observation.StaleAfter);

            entry.Current = snapshot;
            subscribers = entry.Subscribers.Values.ToArray();
        }

        Publish(subscribers, snapshot);
        return new DeviceObservationApplyResult<TState>(
            DeviceObservationApplyStatus.Applied,
            snapshot);
    }

    public IReadOnlyList<DeviceSnapshot<TState>> AdvanceConnectionEpoch(
        string deviceId,
        long connectionEpoch,
        string qualityReason = "rehydrating")
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (connectionEpoch < 0)
            throw new ArgumentOutOfRangeException(nameof(connectionEpoch));

        List<DeviceSnapshot<TState>> changed = [];

        foreach (var pair in _entries)
        {
            if (!pair.Key.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            DeviceSnapshot<TState>? next = null;
            Channel<DeviceSnapshot<TState>>[] subscribers = [];

            lock (pair.Value.Gate)
            {
                var current = pair.Value.Current;
                if (current is null || connectionEpoch <= current.ConnectionEpoch)
                    continue;

                next = current with
                {
                    SnapshotVersion = Interlocked.Increment(ref _snapshotVersion),
                    ConnectionEpoch = connectionEpoch,
                    Source = DeviceObservationSource.Rehydrate,
                    ObservedTimestamp = _timeProvider.GetTimestamp(),
                    ObservedAtUtc = _timeProvider.GetUtcNow(),
                    Quality = DeviceSnapshotQuality.Unknown,
                    QualityReason = qualityReason,
                    SourceSequence = null
                };

                pair.Value.Current = next;
                subscribers = pair.Value.Subscribers.Values.ToArray();
            }

            changed.Add(next);
            Publish(subscribers, next);
        }

        return changed;
    }

    public DeviceSnapshot<TState>? Get(
        string deviceId,
        DeviceStatePartitionKey partition)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ValidatePartition(partition);

        if (!_entries.TryGetValue(new EntryKey(deviceId, partition.Value), out var entry))
            return null;

        lock (entry.Gate)
            return entry.Current is null ? null : ApplyFreshness(entry.Current);
    }

    public IReadOnlyList<DeviceSnapshot<TState>> GetDeviceSnapshots(string deviceId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var snapshots = new List<DeviceSnapshot<TState>>();
        foreach (var pair in _entries)
        {
            if (!pair.Key.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            lock (pair.Value.Gate)
            {
                if (pair.Value.Current is { } current)
                    snapshots.Add(ApplyFreshness(current));
            }
        }

        return snapshots
            .OrderBy(snapshot => snapshot.Partition.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async IAsyncEnumerable<DeviceSnapshot<TState>> Subscribe(
        string deviceId,
        DeviceStatePartitionKey partition,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ValidatePartition(partition);

        var key = new EntryKey(deviceId, partition.Value);
        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        var subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateBounded<DeviceSnapshot<TState>>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        lock (entry.Gate)
        {
            entry.Subscribers.Add(subscriptionId, channel);
            if (entry.Current is { } current)
                channel.Writer.TryWrite(ApplyFreshness(current));
        }

        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return ApplyFreshness(snapshot);
            }
        }
        finally
        {
            lock (entry.Gate)
            {
                entry.Subscribers.Remove(subscriptionId);
                channel.Writer.TryComplete();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        foreach (var entry in _entries.Values)
        {
            lock (entry.Gate)
            {
                foreach (var subscription in entry.Subscribers.Values)
                    subscription.Writer.TryComplete();
                entry.Subscribers.Clear();
            }
        }

        _entries.Clear();
        return ValueTask.CompletedTask;
    }

    private DeviceSnapshot<TState> ApplyFreshness(DeviceSnapshot<TState> snapshot)
    {
        if (snapshot.Quality != DeviceSnapshotQuality.Good ||
            snapshot.StaleAfter is not { } staleAfter)
            return snapshot;

        if (staleAfter <= TimeSpan.Zero)
            return snapshot with
            {
                Quality = DeviceSnapshotQuality.Stale,
                QualityReason = "freshness_timeout"
            };

        return _timeProvider.GetElapsedTime(snapshot.ObservedTimestamp) >= staleAfter
            ? snapshot with
            {
                Quality = DeviceSnapshotQuality.Stale,
                QualityReason = "freshness_timeout"
            }
            : snapshot;
    }

    private static bool IsOlder(
        DeviceObservation<TState> observation,
        DeviceSnapshot<TState> current)
    {
        if (observation.ObservedTimestamp < current.ObservedTimestamp)
            return true;
        if (observation.ObservedTimestamp > current.ObservedTimestamp)
            return false;

        if (observation.Source == current.Source &&
            observation.SourceSequence is { } nextSequence &&
            current.SourceSequence is { } currentSequence)
        {
            return nextSequence <= currentSequence;
        }

        return false;
    }

    private static void Publish(
        IEnumerable<Channel<DeviceSnapshot<TState>>> subscribers,
        DeviceSnapshot<TState> snapshot)
    {
        foreach (var subscriber in subscribers)
            subscriber.Writer.TryWrite(snapshot);
    }

    private static void Validate(DeviceObservation<TState> observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.DeviceId);
        ValidatePartition(observation.Partition);

        if (observation.ConnectionEpoch < 0)
            throw new ArgumentOutOfRangeException(nameof(observation.ConnectionEpoch));
        if (observation.StaleAfter is { } staleAfter && staleAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(observation.StaleAfter));
    }

    private static void ValidatePartition(DeviceStatePartitionKey partition)
    {
        if (string.IsNullOrWhiteSpace(partition.Value))
            throw new ArgumentException("State partition key cannot be empty.", nameof(partition));
    }

    private readonly record struct EntryKey(string DeviceId, string Partition)
    {
        public bool Equals(EntryKey other) =>
            DeviceId.Equals(other.DeviceId, StringComparison.OrdinalIgnoreCase) &&
            Partition.Equals(other.Partition, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(DeviceId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(Partition));
    }

    private sealed class Entry
    {
        public object Gate { get; } = new();
        public DeviceSnapshot<TState>? Current { get; set; }
        public Dictionary<Guid, Channel<DeviceSnapshot<TState>>> Subscribers { get; } = [];
    }
}
