using System.Collections.Concurrent;
using UpperHost.Control.Scheduling;

namespace UpperHost.Control.State;

public enum DeviceControlReadinessState
{
    Offline,
    Rehydrating,
    Ready,
    Faulted
}

public sealed record DeviceControlReadiness(
    string DeviceId,
    long ConnectionEpoch,
    DeviceControlReadinessState State,
    IReadOnlyList<string> PendingRequirements,
    IReadOnlyDictionary<string, string> FailedRequirements)
{
    public bool IsReady => State == DeviceControlReadinessState.Ready;
}

public sealed class DeviceControlStateRegistry :
    IDeviceConnectionEpochSource,
    ICommandConnectionEpochValidator
{
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IDeviceEpochInvalidationSink[] _snapshotSinks;

    public DeviceControlStateRegistry(
        IEnumerable<IDeviceEpochInvalidationSink>? snapshotSinks = null) =>
        _snapshotSinks = snapshotSinks?.ToArray() ?? [];

    public long GetCurrentEpoch(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return _entries.TryGetValue(deviceId, out var entry)
            ? Volatile.Read(ref entry.Epoch)
            : 0;
    }

    public DeviceControlReadiness BeginRehydrate(
        string deviceId,
        IEnumerable<string>? requiredRequirements = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var required = (requiredRequirements ?? [])
            .Where(static requirement => !string.IsNullOrWhiteSpace(requirement))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entry = _entries.GetOrAdd(deviceId, static id => new Entry(id));
        long epoch;
        DeviceControlReadiness snapshot;

        lock (entry.Gate)
        {
            epoch = ++entry.Epoch;
            entry.Pending.Clear();
            entry.Failures.Clear();

            foreach (var requirement in required)
                entry.Pending.Add(requirement);

            entry.State = required.Length == 0
                ? DeviceControlReadinessState.Ready
                : DeviceControlReadinessState.Rehydrating;
            snapshot = entry.Snapshot();
        }

        foreach (var sink in _snapshotSinks)
            sink.AdvanceConnectionEpoch(deviceId, epoch, "rehydrating");

        return snapshot;
    }

    public DeviceControlReadiness MarkRequirementSatisfied(
        string deviceId,
        long connectionEpoch,
        string requirement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);

        var entry = GetRequiredEntry(deviceId);
        lock (entry.Gate)
        {
            if (connectionEpoch != entry.Epoch)
                return entry.Snapshot();

            entry.Pending.Remove(requirement);
            entry.Failures.Remove(requirement);

            if (entry.Pending.Count == 0 && entry.Failures.Count == 0)
                entry.State = DeviceControlReadinessState.Ready;

            return entry.Snapshot();
        }
    }

    public DeviceControlReadiness MarkRequirementFailed(
        string deviceId,
        long connectionEpoch,
        string requirement,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var entry = GetRequiredEntry(deviceId);
        lock (entry.Gate)
        {
            if (connectionEpoch != entry.Epoch)
                return entry.Snapshot();

            entry.Pending.Remove(requirement);
            entry.Failures[requirement] = reason;
            entry.State = DeviceControlReadinessState.Faulted;
            return entry.Snapshot();
        }
    }

    public DeviceControlReadiness MarkDisconnected(
        string deviceId,
        string reason = "disconnected")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var entry = _entries.GetOrAdd(deviceId, static id => new Entry(id));
        long epoch;
        DeviceControlReadiness snapshot;

        lock (entry.Gate)
        {
            epoch = ++entry.Epoch;
            entry.Pending.Clear();
            entry.Failures.Clear();
            entry.State = DeviceControlReadinessState.Offline;
            snapshot = entry.Snapshot();
        }

        foreach (var sink in _snapshotSinks)
            sink.AdvanceConnectionEpoch(deviceId, epoch, reason);

        return snapshot;
    }

    public DeviceControlReadiness GetReadiness(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return _entries.TryGetValue(deviceId, out var entry)
            ? ReadSnapshot(entry)
            : new DeviceControlReadiness(
                deviceId,
                0,
                DeviceControlReadinessState.Offline,
                [],
                new Dictionary<string, string>());
    }

    public bool IsCurrent(
        long connectionEpoch,
        IReadOnlyList<CommandResourceKey>? resources)
    {
        if (resources is null || resources.Count == 0)
            return true;

        var deviceIds = resources
            .Where(static resource =>
                resource.Category.Equals("device", StringComparison.OrdinalIgnoreCase))
            .Select(static resource => resource.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (deviceIds.Length == 0)
            return true;

        foreach (var deviceId in deviceIds)
        {
            if (GetCurrentEpoch(deviceId) != connectionEpoch)
                return false;
        }

        return true;
    }

    private Entry GetRequiredEntry(string deviceId) =>
        _entries.TryGetValue(deviceId, out var entry)
            ? entry
            : throw new InvalidOperationException(
                $"Device '{deviceId}' has no active control-state epoch.");

    private static DeviceControlReadiness ReadSnapshot(Entry entry)
    {
        lock (entry.Gate)
            return entry.Snapshot();
    }

    private sealed class Entry(string deviceId)
    {
        public object Gate { get; } = new();
        public string DeviceId { get; } = deviceId;
        public long Epoch;
        public DeviceControlReadinessState State = DeviceControlReadinessState.Offline;
        public HashSet<string> Pending { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Failures { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public DeviceControlReadiness Snapshot() =>
            new(
                DeviceId,
                Epoch,
                State,
                Pending.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                new Dictionary<string, string>(
                    Failures,
                    StringComparer.OrdinalIgnoreCase));
    }
}
