using System.Threading.Channels;

namespace OpenDeviceStudio.Control.State;

public enum PollMissedTickPolicy
{
    Skip,
    Coalesce
}

public sealed record DevicePollRuntimeOptions(
    int WorkCapacity = 64,
    int MaxConcurrency = 4)
{
    internal void Validate()
    {
        if (WorkCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(WorkCapacity));
        if (MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
    }
}

public sealed record DevicePollContext(
    string GroupId,
    string DeviceId,
    DeviceStatePartitionKey Partition,
    long ConnectionEpoch,
    long ObservedTimestamp);

public sealed record DevicePollSample<TState>(
    TState Value,
    DeviceSnapshotQuality Quality = DeviceSnapshotQuality.Good,
    long? SourceSequence = null,
    DateTimeOffset? ObservedAtUtc = null,
    string? QualityReason = null);

public delegate ValueTask<DevicePollSample<TState>> DevicePollOperation<TState>(
    DevicePollContext context,
    CancellationToken cancellationToken);

public interface IDeviceConnectionEpochSource
{
    long GetCurrentEpoch(string deviceId);
}

public sealed record DevicePollGroup<TState>(
    string GroupId,
    string DeviceId,
    DeviceStatePartitionKey Partition,
    TimeSpan Interval,
    TimeSpan Timeout,
    TimeSpan StaleAfter,
    DevicePollOperation<TState> Operation,
    TimeSpan InitialStagger = default,
    PollMissedTickPolicy MissedTickPolicy = PollMissedTickPolicy.Coalesce);

/// <summary>
/// Host-owned polling scheduler with one bounded schedule calendar and one bounded work queue.
/// Poll groups never accumulate historical missed ticks and each group has at most one
/// queued/running invocation plus an optional coalesced follow-up.
/// </summary>
public sealed class DevicePollRuntime<TState> : IAsyncDisposable
{
    private readonly DeviceSnapshotStore<TState> _store;
    private readonly IDeviceConnectionEpochSource _epochSource;
    private readonly DevicePollRuntimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeGroup[] _groups;
    private readonly Channel<RuntimeGroup> _work;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _scheduler;
    private Task[] _workers = [];
    private int _state;

    public DevicePollRuntime(
        IEnumerable<DevicePollGroup<TState>> groups,
        DeviceSnapshotStore<TState> store,
        IDeviceConnectionEpochSource epochSource,
        DevicePollRuntimeOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _epochSource = epochSource ?? throw new ArgumentNullException(nameof(epochSource));
        _options = options ?? new DevicePollRuntimeOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;

        _groups = groups.Select(static group => new RuntimeGroup(group)).ToArray();
        ValidateGroups(_groups);

        _work = Channel.CreateBounded<RuntimeGroup>(
            new BoundedChannelOptions(_options.WorkCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = _options.MaxConcurrency == 1,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    }

    public bool IsRunning => Volatile.Read(ref _state) == 1;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Polling runtime can only be started once.");

        var now = _timeProvider.GetTimestamp();
        foreach (var group in _groups)
            group.NextDueTimestamp = AddTimestamp(now, group.Definition.InitialStagger);

        _workers = Enumerable.Range(0, _options.MaxConcurrency)
            .Select(_ => RunWorkerAsync(_shutdown.Token))
            .ToArray();
        _scheduler = RunSchedulerAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var previous = Interlocked.Exchange(ref _state, 2);
        if (previous == 3)
            return;

        _shutdown.Cancel();

        if (_scheduler is not null)
        {
            try
            {
                await _scheduler.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
        }

        _work.Writer.TryComplete();

        if (_workers.Length > 0)
        {
            try
            {
                await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
        }

        Interlocked.Exchange(ref _state, 3);
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _state) is 0 or 1)
            await StopAsync().ConfigureAwait(false);

        _shutdown.Dispose();
    }

    private async Task RunSchedulerAsync(CancellationToken cancellationToken)
    {
        var schedule = new PriorityQueue<RuntimeGroup, long>();
        foreach (var group in _groups)
            schedule.Enqueue(group, group.NextDueTimestamp);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!schedule.TryPeek(out var group, out var due))
                    break;

                var now = _timeProvider.GetTimestamp();
                if (due > now)
                {
                    var delay = _timeProvider.GetElapsedTime(now, due);
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                schedule.Dequeue();
                now = _timeProvider.GetTimestamp();
                group.NextDueTimestamp = NextDueAfter(due, group.Definition.Interval, now);
                schedule.Enqueue(group, group.NextDueTimestamp);

                if (Interlocked.CompareExchange(ref group.QueuedOrRunning, 1, 0) != 0)
                {
                    if (group.Definition.MissedTickPolicy == PollMissedTickPolicy.Coalesce)
                    {
                        Interlocked.Exchange(ref group.CoalescedFollowUp, 1);
                        DeviceControlTelemetry.PollDeferred.Add(
                            1,
                            DeviceControlTelemetry.Tags("poll.schedule", "coalesced"));
                    }
                    else
                    {
                        DeviceControlTelemetry.PollDeferred.Add(
                            1,
                            DeviceControlTelemetry.Tags("poll.schedule", "skipped"));
                    }
                    continue;
                }

                if (!_work.Writer.TryWrite(group))
                {
                    Interlocked.Exchange(ref group.QueuedOrRunning, 0);
                    DeviceControlTelemetry.PollDeferred.Add(
                        1,
                        DeviceControlTelemetry.Tags("poll.schedule", "queue_full"));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.Writer.TryComplete();
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var group in _work.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await ExecutePollAsync(group, cancellationToken).ConfigureAwait(false);

                if (Interlocked.Exchange(ref group.CoalescedFollowUp, 0) != 0 &&
                    !cancellationToken.IsCancellationRequested)
                {
                    if (_work.Writer.TryWrite(group))
                        continue;

                    DeviceControlTelemetry.PollDeferred.Add(
                        1,
                        DeviceControlTelemetry.Tags("poll.schedule", "queue_full"));
                }

                Interlocked.Exchange(ref group.QueuedOrRunning, 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExecutePollAsync(RuntimeGroup group, CancellationToken runtimeToken)
    {
        var started = _timeProvider.GetTimestamp();
        var outcome = "fault";
        var observedTimestamp = started;
        var epoch = _epochSource.GetCurrentEpoch(group.Definition.DeviceId);
        var context = new DevicePollContext(
            group.Definition.GroupId,
            group.Definition.DeviceId,
            group.Definition.Partition,
            epoch,
            observedTimestamp);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(runtimeToken);
        using var timer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            timeoutCts,
            group.Definition.Timeout,
            Timeout.InfiniteTimeSpan);

        try
        {
            var sample = await group.Definition.Operation(context, timeoutCts.Token)
                .ConfigureAwait(false);

            if (_epochSource.GetCurrentEpoch(group.Definition.DeviceId) != epoch)
            {
                outcome = "stale_epoch";
                return;
            }

            var applied = _store.Apply(new DeviceObservation<TState>(
                group.Definition.DeviceId,
                group.Definition.Partition,
                epoch,
                DeviceObservationSource.Poll,
                observedTimestamp,
                sample.Value,
                sample.Quality,
                sample.SourceSequence,
                sample.ObservedAtUtc ?? _timeProvider.GetUtcNow(),
                sample.QualityReason,
                group.Definition.StaleAfter));
            outcome = applied.Status == DeviceObservationApplyStatus.Applied
                ? "success"
                : applied.Status.ToString();
        }
        catch (OperationCanceledException) when (
            runtimeToken.IsCancellationRequested ||
            timeoutCts.IsCancellationRequested)
        {
            outcome = runtimeToken.IsCancellationRequested ? "cancelled" : "timeout";
            // Keep the last successful snapshot authoritative. Freshness will
            // naturally transition it to Stale without inventing a new value.
        }
        catch
        {
            outcome = "fault";
            // Provider faults are isolated to this poll invocation. The last
            // successful snapshot remains authoritative and becomes stale by age.
        }
        finally
        {
            DeviceControlTelemetry.PollExecutions.Add(
                1,
                DeviceControlTelemetry.Tags("poll.execute", outcome));
            DeviceControlTelemetry.PollDurationSeconds.Record(
                _timeProvider.GetElapsedTime(started).TotalSeconds,
                DeviceControlTelemetry.Tags("poll.execute", outcome));
        }
    }

    private long NextDueAfter(long previousDue, TimeSpan interval, long now)
    {
        var next = AddTimestamp(previousDue, interval);
        while (next <= now)
            next = AddTimestamp(next, interval);
        return next;
    }

    private long AddTimestamp(long timestamp, TimeSpan delay)
    {
        if (delay == TimeSpan.Zero)
            return timestamp;

        var delta = checked((long)Math.Ceiling(
            delay.TotalSeconds * _timeProvider.TimestampFrequency));
        return checked(timestamp + Math.Max(1, delta));
    }

    private static void ValidateGroups(RuntimeGroup[] groups)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtime in groups)
        {
            var group = runtime.Definition;
            ArgumentException.ThrowIfNullOrWhiteSpace(group.GroupId);
            ArgumentException.ThrowIfNullOrWhiteSpace(group.DeviceId);
            if (string.IsNullOrWhiteSpace(group.Partition.Value))
                throw new ArgumentException("Poll partition cannot be empty.", nameof(groups));
            if (group.Interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(groups), "Poll interval must be greater than zero.");
            if (group.Timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(groups), "Poll timeout must be greater than zero.");
            if (group.StaleAfter < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(groups), "StaleAfter cannot be negative.");
            if (group.InitialStagger < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(groups), "InitialStagger cannot be negative.");
            ArgumentNullException.ThrowIfNull(group.Operation);

            if (!ids.Add(group.GroupId))
                throw new ArgumentException($"Duplicate poll group id '{group.GroupId}'.", nameof(groups));
        }
    }

    private sealed class RuntimeGroup(DevicePollGroup<TState> definition)
    {
        public DevicePollGroup<TState> Definition { get; } = definition;
        public long NextDueTimestamp;
        public int QueuedOrRunning;
        public int CoalescedFollowUp;
    }
}
